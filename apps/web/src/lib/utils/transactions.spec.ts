import { describe, expect, it } from 'vitest';
import type { BillPayment, InboxItem, TransferPair } from '#lib/types.ts';
import {
	accountTxQuery,
	billNameFor,
	candidateSelectable,
	flowLabel,
	groupByDay,
	ignoreReasonLabel,
	itemFromPairSide,
	itemFromPayment,
	kindForAmount,
	netBars,
	pairsByDate,
	paymentSource,
	scoreBar,
	statusChip,
	transferArrow,
	txState
} from './transactions.ts';

function row(patch: Partial<InboxItem> = {}): InboxItem {
	return {
		id: 't1',
		source: 'simplefin',
		accountId: 'a1',
		accountName: 'Everyday',
		postedOn: '2026-10-20',
		amountCents: -4000,
		description: 'THE DINER',
		payee: null,
		memo: null,
		pending: false,
		status: 'pending',
		suggestion: null,
		confirmed: null,
		createdAt: '2026-10-20T10:00:00Z',
		ignoreReason: null,
		transfer: null,
		match: null,
		expense: null,
		...patch
	};
}
const confirmed = {
	expenseId: null,
	itemId: null,
	dueDate: null,
	by: null,
	byName: null,
	at: null,
	auto: false
};

describe('txState / statusChip', () => {
	it('labels a row to review, and a pending one as Pending on lists', () => {
		expect(txState(row())).toEqual({ kind: 'review', label: 'To review' });
		expect(statusChip(row({ pending: true }))).toEqual({ label: 'Pending', kind: 'pending' });
	});

	it('names the expense category and the paid bill', () => {
		const expense = row({
			status: 'confirmed',
			confirmed: { ...confirmed, expenseId: 'e1', categoryName: 'Fun' },
			expense: { expenseId: 'e1', categoryId: 'c1', categoryName: 'Fun' }
		});
		expect(txState(expense).label).toBe('Expense in Fun');
		expect(statusChip(expense).label).toBe('Fun');

		const paid = row({
			status: 'confirmed',
			confirmed: { ...confirmed, itemId: 'i1', dueDate: '2026-11-01', auto: true },
			match: { itemId: 'i1', itemName: 'Rent', itemKind: 'bill', dueDate: '2026-11-01' }
		});
		expect(txState(paid).kind).toBe('paid');
		expect(txState(paid).label).toMatch(/^Paid Rent · .*Nov 1.*\(auto\)$/);
		expect(statusChip(paid).label).toBe('Rent');
	});

	it('falls back to the v3 confirmation when the v5 fields are missing', () => {
		const v3 = row({
			status: 'confirmed',
			match: undefined,
			expense: undefined,
			confirmed: { ...confirmed, itemId: 'i1', dueDate: null, itemName: 'Gym' }
		});
		expect(txState(v3).label).toBe('Paid Gym');
	});

	it('marks transfers with the other side and ignore reasons', () => {
		const t = row({
			status: 'ignored',
			ignoreReason: 'transfer',
			transfer: { peerId: 't2', peerAccountId: 'a2', peerAccountName: 'Savings', auto: true }
		});
		expect(txState(t)).toEqual({ kind: 'transfer', label: 'Transfer → Savings (auto)' });
		expect(statusChip(t).label).toBe('Transfer → Savings');
		expect(txState(row({ status: 'ignored', ignoreReason: 'dropped' })).label).toBe(
			'Dropped authorization'
		);
		expect(txState(row({ status: 'ignored', ignoreReason: 'manual' })).label).toBe('Ignored');
	});

	it('treats a typed expense as manual', () => {
		const manual = { ...row({ source: 'manual', status: 'confirmed' }), kind: 'manual' as const };
		expect(txState(manual).kind).toBe('manual');
		expect(statusChip({ ...manual, expense: null }).label).toBe('Manual');
	});
});

describe('transferArrow / ignoreReasonLabel', () => {
	it('points out for money out and in for money in; one-sided reads Other account', () => {
		const peer = { peerId: 'x', peerAccountId: 'a2', peerAccountName: 'Savings', auto: false };
		expect(transferArrow(row({ transfer: peer }))).toBe('→ Savings');
		expect(transferArrow(row({ amountCents: 4000, transfer: peer }))).toBe('← Savings');
		expect(transferArrow(row({ transfer: null }))).toBe('→ Other account');
	});

	it('labels every ignore reason for the Ignored filter', () => {
		const auto = { peerId: null, peerAccountId: null, peerAccountName: null, auto: true };
		expect(ignoreReasonLabel({ ignoreReason: 'transfer', transfer: auto })).toBe('Transfer · auto');
		expect(
			ignoreReasonLabel({ ignoreReason: 'transfer', transfer: { ...auto, auto: false } })
		).toBe('Transfer');
		expect(ignoreReasonLabel({ ignoreReason: 'dropped', transfer: null })).toBe(
			'Dropped authorization'
		);
		expect(ignoreReasonLabel({ ignoreReason: 'manual', transfer: null })).toBe('Ignored');
		expect(ignoreReasonLabel({ ignoreReason: undefined, transfer: undefined })).toBe('Ignored');
	});
});

describe('candidates', () => {
	it('clamps the score bar and picks a tone', () => {
		expect(scoreBar(95)).toEqual({ percent: 95, label: 'Strong match (95)', tone: 'strong' });
		expect(scoreBar(60).tone).toBe('good');
		expect(scoreBar(12).tone).toBe('weak');
		expect(scoreBar(140).percent).toBe(100);
		expect(scoreBar(-5).percent).toBe(0);
		expect(scoreBar(Number.NaN).percent).toBe(0);
	});

	it('only lets a paid occurrence be picked when this transaction paid it', () => {
		const paid = { paidAt: 'x', paidBy: null, amountCents: 1, auto: false };
		expect(candidateSelectable({ paid: null, paidByThis: false })).toBe(true);
		expect(candidateSelectable({ paid, paidByThis: false })).toBe(false);
		expect(candidateSelectable({ paid, paidByThis: true })).toBe(true);
	});
});

describe('groupByDay', () => {
	it('groups consecutive days and sums the signed net', () => {
		const groups = groupByDay([
			{ postedOn: '2026-10-21', amountCents: -500 },
			{ postedOn: '2026-10-21', amountCents: 2000 },
			{ postedOn: '2026-10-20', amountCents: -100 }
		]);
		expect(groups.map((g) => [g.day, g.items.length, g.netCents])).toEqual([
			['2026-10-21', 2, 1500],
			['2026-10-20', 1, -100]
		]);
		expect(groupByDay([])).toEqual([]);
	});
});

describe('create a bill from this', () => {
	it('defaults the kind from the sign and the name from payee, else description', () => {
		expect(kindForAmount(-1)).toBe('bill');
		expect(kindForAmount(1)).toBe('income');
		expect(billNameFor({ payee: 'Planet Fitness', description: 'PLANET FIT 123' })).toBe(
			'Planet Fitness'
		);
		expect(billNameFor({ payee: null, description: `  ${'x'.repeat(90)} ` })).toHaveLength(80);
	});
});

describe('transfer report helpers', () => {
	const pair = (id: string, date: string): TransferPair => ({
		id,
		date,
		amountCents: 50000,
		from: { accountId: 'a1', name: 'Everyday', transactionId: `${id}-out` },
		to: { accountId: 'a2', name: 'Savings', transactionId: `${id}-in` },
		auto: true
	});

	it('labels flows, one-sided sides read Other account', () => {
		expect(flowLabel({ fromName: 'Everyday', toName: 'Savings' })).toBe('Everyday → Savings');
		expect(flowLabel({ fromName: '', toName: 'Savings' })).toBe('Other account → Savings');
	});

	it('scales diverging bars against the largest side', () => {
		const bars = netBars([
			{ accountId: 'a1', name: 'Everyday', inCents: 0, outCents: 80000, netCents: -80000 },
			{ accountId: 'a2', name: 'Savings', inCents: 40000, outCents: 0, netCents: 40000 }
		]);
		expect(bars[0].outPct).toBe(100);
		expect(bars[1].inPct).toBe(50);
		expect(
			netBars([{ accountId: 'a', name: 'A', inCents: 0, outCents: 0, netCents: 0 }])[0].inPct
		).toBe(0);
	});

	it('groups pairs by date, newest first', () => {
		const days = pairsByDate([
			pair('p1', '2026-10-01'),
			pair('p2', '2026-10-05'),
			pair('p3', '2026-10-05')
		]);
		expect(days.map((d) => [d.day, d.pairs.length])).toEqual([
			['2026-10-05', 2],
			['2026-10-01', 1]
		]);
	});

	it('builds a signed sheet row for either side of a pair', () => {
		const out = itemFromPairSide(pair('p1', '2026-10-01'), 'from');
		expect(out.id).toBe('p1-out');
		expect(out.amountCents).toBe(-50000);
		expect(out.transfer?.peerAccountName).toBe('Savings');
		const inn = itemFromPairSide(pair('p1', '2026-10-01'), 'to');
		expect(inn.amountCents).toBe(50000);
		expect(txState(inn).label).toBe('Transfer ← Everyday (auto)');
	});
});

describe('bill payments', () => {
	const bill = { id: 'i1', name: 'Rent', kind: 'bill' as const };
	const paid = (source: 'manual' | 'autopay' | 'bank', auto = false) => ({
		paidAt: '2026-10-01T00:00:00Z',
		paidBy: null,
		amountCents: 185000,
		auto,
		source
	});

	it('names the source of a payment', () => {
		const p: BillPayment = {
			dueDate: '2026-10-01',
			plannedCents: 185000,
			paid: paid('bank'),
			varianceCents: 0,
			transaction: {
				id: 't1',
				postedOn: '2026-10-01',
				amountCents: -185000,
				description: 'RENT PAYMENT',
				accountName: 'Everyday'
			}
		};
		expect(paymentSource(p)).toBe('RENT PAYMENT · Everyday');
		expect(paymentSource({ ...p, transaction: null, paid: paid('autopay', true) })).toBe('Autopay');
		expect(paymentSource({ ...p, transaction: null, paid: paid('manual') })).toBe('Manual');
		expect(paymentSource({ ...p, transaction: null, paid: null })).toBe('');
	});

	it('turns a bank payment into a matched sheet row', () => {
		const p: BillPayment = {
			dueDate: '2026-10-01',
			plannedCents: 185000,
			paid: paid('bank'),
			varianceCents: 0,
			transaction: {
				id: 't1',
				postedOn: '2026-10-02',
				amountCents: -185000,
				description: 'RENT PAYMENT',
				accountName: 'Everyday'
			}
		};
		const item = itemFromPayment(bill, p);
		expect(item?.id).toBe('t1');
		expect(item && txState(item).kind).toBe('paid');
		expect(itemFromPayment(bill, { ...p, transaction: null })).toBeNull();
	});
});

describe('accountTxQuery', () => {
	it('omits defaults and encodes the rest', () => {
		expect(accountTxQuery({})).toBe('');
		expect(accountTxQuery({ filter: 'all', limit: 50 })).toBe('?limit=50');
		expect(
			accountTxQuery({
				filter: 'review',
				q: 'kroger & co',
				month: '2026-10',
				before: '2026-10-01|x'
			})
		).toBe('?filter=review&q=kroger+%26+co&month=2026-10&before=2026-10-01%7Cx');
	});
});
