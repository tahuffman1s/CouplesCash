/**
 * v5 transaction helpers (pure, unit-tested): what state a bank row is in, the status chip on
 * account lists, ignore-reason labels, candidate score bars, day groups and the shapes the
 * transaction sheet needs when it is opened from a bill payment or a transfer pair.
 */
import type {
	BillPayment,
	TxFilter,
	InboxItem,
	Kind,
	MatchCandidate,
	RecurringItem,
	TransferFlow,
	TransferNet,
	TransferPair
} from '#lib/types.ts';
import { formatDate } from './dates.ts';

export type TxStateKind =
	'review' | 'expense' | 'paid' | 'transfer' | 'ignored' | 'dropped' | 'superseded' | 'manual';
export type TxState = { kind: TxStateKind; label: string };

/** True when the row is (or was marked as) one side of a transfer. */
export function isTransfer(item: Pick<InboxItem, 'transfer' | 'ignoreReason'>): boolean {
	return !!item.transfer || item.ignoreReason === 'transfer';
}

/** "→ Savings" for money out, "← Checking" for money in, "Other account" when one-sided. */
export function transferArrow(item: Pick<InboxItem, 'amountCents' | 'transfer'>): string {
	const peer = item.transfer?.peerAccountName || 'Other account';
	return `${item.amountCents < 0 ? '→' : '←'} ${peer}`;
}

/** Which bill/income a confirmed row paid, from the v5 `match` or the v3 `confirmed` fallback. */
export function matchedTo(
	item: Pick<InboxItem, 'match' | 'confirmed' | 'suggestion'>
): { itemId: string; itemName: string; dueDate: string | null } | null {
	if (item.match)
		return {
			itemId: item.match.itemId,
			itemName: item.match.itemName,
			dueDate: item.match.dueDate
		};
	const c = item.confirmed;
	if (!c?.itemId) return null;
	const name =
		c.itemName ??
		(item.suggestion?.kind === 'occurrence' && item.suggestion.itemId === c.itemId
			? item.suggestion.itemName
			: 'bill');
	return { itemId: c.itemId, itemName: name, dueDate: c.dueDate };
}

/** Category of the expense a row was confirmed as (v5 `expense`, else v3 `confirmed`). */
export function expenseCategory(
	item: Pick<InboxItem, 'expense' | 'confirmed' | 'suggestion'>
): string | null {
	if (item.expense) return item.expense.categoryName;
	const c = item.confirmed;
	if (!c?.expenseId) return null;
	return (
		c.categoryName ?? (item.suggestion?.kind === 'category' ? item.suggestion.categoryName : null)
	);
}

/** The sheet's "current state" line: To review / Expense in Groceries / Paid Rent · Nov 1 / … */
export function txState(item: InboxItem & { kind?: 'bank' | 'manual' }): TxState {
	if (item.kind === 'manual' || item.source === 'manual') {
		const cat = expenseCategory(item);
		return { kind: 'manual', label: cat ? `Manual expense in ${cat}` : 'Manual expense' };
	}
	if (isTransfer(item) && item.status !== 'pending') {
		return {
			kind: 'transfer',
			label: `Transfer ${transferArrow(item)}${item.transfer?.auto ? ' (auto)' : ''}`
		};
	}
	if (item.status === 'pending') return { kind: 'review', label: 'To review' };
	if (item.status === 'ignored') {
		if (item.ignoreReason === 'dropped') return { kind: 'dropped', label: 'Dropped authorization' };
		if (item.ignoreReason === 'superseded') return { kind: 'superseded', label: 'Replaced' };
		return { kind: 'ignored', label: 'Ignored' };
	}
	const m = matchedTo(item);
	if (m) {
		const auto = item.confirmed?.auto ? ' (auto)' : '';
		return {
			kind: 'paid',
			label: `Paid ${m.itemName}${m.dueDate ? ` · ${formatDate(m.dueDate)}` : ''}${auto}`
		};
	}
	const cat = expenseCategory(item);
	return { kind: 'expense', label: cat ? `Expense in ${cat}` : 'Expense' };
}

/** Short chip for list rows: "To review", "Groceries", "Rent", "Transfer → Savings", "Pending". */
export function statusChip(item: InboxItem & { kind?: 'bank' | 'manual' }): {
	label: string;
	kind: TxStateKind | 'pending';
} {
	const s = txState(item);
	if (item.pending && s.kind === 'review') return { label: 'Pending', kind: 'pending' };
	switch (s.kind) {
		case 'manual':
			return { label: expenseCategory(item) ?? 'Manual', kind: 'manual' };
		case 'expense':
			return { label: expenseCategory(item) ?? 'Expense', kind: 'expense' };
		case 'paid':
			return { label: matchedTo(item)?.itemName ?? 'Paid', kind: 'paid' };
		case 'transfer':
			return { label: `Transfer ${transferArrow(item)}`, kind: 'transfer' };
		default:
			return { label: s.label, kind: s.kind };
	}
}

/** Reason label for the inbox's Ignored filter. */
export function ignoreReasonLabel(item: Pick<InboxItem, 'ignoreReason' | 'transfer'>): string {
	switch (item.ignoreReason) {
		case 'transfer':
			return item.transfer?.auto ? 'Transfer · auto' : 'Transfer';
		case 'dropped':
			return 'Dropped authorization';
		case 'superseded':
			return 'Replaced by the posted transaction';
		default:
			return item.transfer ? (item.transfer.auto ? 'Transfer · auto' : 'Transfer') : 'Ignored';
	}
}

/** Score bar for a ranked candidate: clamped width, a word for screen readers, a tone. */
export function scoreBar(score: number): {
	percent: number;
	label: string;
	tone: 'strong' | 'good' | 'weak';
} {
	const n = Number.isFinite(score) ? Math.round(score) : 0;
	const percent = Math.max(0, Math.min(100, n));
	if (percent >= 80) return { percent, label: `Strong match (${percent})`, tone: 'strong' };
	if (percent >= 50) return { percent, label: `Possible match (${percent})`, tone: 'good' };
	return { percent, label: `Weak match (${percent})`, tone: 'weak' };
}

/** A paid occurrence is only selectable when it was paid by this very transaction. */
export function candidateSelectable(c: Pick<MatchCandidate, 'paid' | 'paidByThis'>): boolean {
	return !c.paid || c.paidByThis;
}

/** Groups rows (already newest first) by `postedOn`, with each day's signed net. */
export function groupByDay<T extends { postedOn: string; amountCents: number }>(
	rows: T[]
): { day: string; items: T[]; netCents: number }[] {
	const out: { day: string; items: T[]; netCents: number }[] = [];
	for (const r of rows) {
		const last = out[out.length - 1];
		if (last && last.day === r.postedOn) {
			last.items.push(r);
			last.netCents += r.amountCents;
		} else out.push({ day: r.postedOn, items: [r], netCents: r.amountCents });
	}
	return out;
}

/** Kind for "Create a bill from this": money out → bill, money in → income. */
export function kindForAmount(amountCents: number): Kind {
	return amountCents < 0 ? 'bill' : 'income';
}

/** Name prefilled for "Create a bill from this" (payee, else description, ≤ 80 chars). */
export function billNameFor(item: Pick<InboxItem, 'payee' | 'description'>): string {
	return (item.payee || item.description || '').trim().slice(0, 80);
}

/** "Checking → Savings" for a flow; one-sided sides read "Other account". */
export function flowLabel(f: Pick<TransferFlow, 'fromName' | 'toName'>): string {
	return `${f.fromName || 'Other account'} → ${f.toName || 'Other account'}`;
}

/** Diverging bar scale: each side as a percent of the largest in/out across accounts. */
export function netBars(rows: TransferNet[]): (TransferNet & { inPct: number; outPct: number })[] {
	const max = Math.max(1, ...rows.flatMap((r) => [Math.abs(r.inCents), Math.abs(r.outCents)]));
	return rows.map((r) => ({
		...r,
		inPct: (Math.abs(r.inCents) / max) * 100,
		outPct: (Math.abs(r.outCents) / max) * 100
	}));
}

/** Pairs grouped by date, newest first. */
export function pairsByDate(pairs: TransferPair[]): { day: string; pairs: TransferPair[] }[] {
	const sorted = [...pairs].sort((a, b) => b.date.localeCompare(a.date));
	const out: { day: string; pairs: TransferPair[] }[] = [];
	for (const p of sorted) {
		const last = out[out.length - 1];
		if (last && last.day === p.date) last.pairs.push(p);
		else out.push({ day: p.date, pairs: [p] });
	}
	return out;
}

/**
 * A minimal InboxItem for one side of a transfer pair, used when the full row can't be loaded.
 * The sheet then offers "Not a transfer" on it.
 */
export function itemFromPairSide(pair: TransferPair, side: 'from' | 'to'): InboxItem {
	const me = pair[side];
	const peer = side === 'from' ? pair.to : pair.from;
	return {
		id: me.transactionId,
		source: 'simplefin',
		accountId: me.accountId,
		accountName: me.name,
		postedOn: pair.date,
		amountCents: side === 'from' ? -Math.abs(pair.amountCents) : Math.abs(pair.amountCents),
		description: side === 'from' ? `Transfer to ${peer.name}` : `Transfer from ${peer.name}`,
		payee: null,
		memo: null,
		pending: false,
		status: 'ignored',
		suggestion: null,
		confirmed: null,
		createdAt: pair.date,
		ignoreReason: 'transfer',
		transfer: {
			peerId: peer.transactionId,
			peerAccountId: peer.accountId,
			peerAccountName: peer.name,
			auto: pair.auto
		},
		match: null,
		expense: null
	};
}

/** A minimal InboxItem for the bank transaction behind a bill payment (sheet from /bills/[id]). */
export function itemFromPayment(bill: Pick<RecurringItem, 'id' | 'name' | 'kind'>, p: BillPayment) {
	const t = p.transaction;
	if (!t) return null;
	const item: InboxItem = {
		id: t.id,
		source: 'simplefin',
		accountId: null,
		accountName: t.accountName,
		postedOn: t.postedOn,
		amountCents: t.amountCents,
		description: t.description,
		payee: null,
		memo: null,
		pending: false,
		status: 'confirmed',
		suggestion: null,
		confirmed: {
			expenseId: null,
			itemId: bill.id,
			dueDate: p.dueDate,
			by: p.paid?.paidBy ?? null,
			byName: null,
			at: p.paid?.paidAt ?? null,
			auto: !!p.paid?.auto,
			itemName: bill.name
		},
		createdAt: t.postedOn,
		ignoreReason: null,
		transfer: null,
		match: { itemId: bill.id, itemName: bill.name, itemKind: bill.kind, dueDate: p.dueDate },
		expense: null
	};
	return item;
}

/** Where a bill payment came from: the bank row, a person, or autopay. */
export function paymentSource(p: BillPayment): string {
	if (!p.paid) return '';
	if (p.transaction)
		return `${p.transaction.description}${p.transaction.accountName ? ` · ${p.transaction.accountName}` : ''}`;
	const src = p.paid.source ?? (p.paid.auto ? 'autopay' : 'manual');
	if (src === 'autopay') return 'Autopay';
	if (src === 'bank') return 'Bank';
	return 'Manual';
}

export type AccountTxQuery = {
	filter?: TxFilter;
	q?: string | null;
	month?: string | null;
	before?: string | null;
	limit?: number;
};
/** `?filter=&q=&month=&before=&limit=` for `GET /accounts/{id}/transactions` (server load and browser). */
export function accountTxQuery(opts: AccountTxQuery): string {
	const q = new URLSearchParams();
	if (opts.filter && opts.filter !== 'all') q.set('filter', opts.filter);
	if (opts.q) q.set('q', opts.q);
	if (opts.month) q.set('month', opts.month);
	if (opts.before) q.set('before', opts.before);
	if (opts.limit) q.set('limit', String(opts.limit));
	const qs = q.toString();
	return qs ? `?${qs}` : '';
}
