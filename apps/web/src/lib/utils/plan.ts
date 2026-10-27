/**
 * v7 Friday plan helpers (pure, unit-tested). The API does the arithmetic (`GET /plan`); these
 * only word and order it the way her note reads: "US Bank, available now", "− Bills before next
 * Friday", "= Left on Thursday". Weekday names always come from the household's payday.
 */
import type {
	AccountRole,
	ChecklistItem,
	Plan,
	PlanAccount,
	PlanBillsAccount,
	PlanLine
} from '#lib/types.ts';
import { addDays, formatTimestamp, parseISODate, weekday, type ISODate } from './dates.ts';
import { formatMoney, signedMoney } from './money.ts';

export const WEEKDAYS_LONG = [
	'Sunday',
	'Monday',
	'Tuesday',
	'Wednesday',
	'Thursday',
	'Friday',
	'Saturday'
] as const;
export const WEEKDAYS_SHORT = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'] as const;
const MONTHS_SHORT = [
	'Jan',
	'Feb',
	'Mar',
	'Apr',
	'May',
	'Jun',
	'Jul',
	'Aug',
	'Sep',
	'Oct',
	'Nov',
	'Dec'
] as const;

/** 0..6 → "Friday" (anything out of range wraps, so a bad value never throws). */
export function weekdayName(n: number, style: 'long' | 'short' = 'long'): string {
	const i = (((Math.trunc(n) || 0) % 7) + 7) % 7;
	return style === 'long' ? WEEKDAYS_LONG[i] : WEEKDAYS_SHORT[i];
}

/** The payday that starts the pay week containing `date` (the most recent payday, today counts). */
export function payWeekStart(date: ISODate, paydayWeekday: number): ISODate {
	const back = (weekday(date) - paydayWeekday + 14) % 7;
	return addDays(date, -back);
}

/** Last day of the pay week (the day before the next payday). */
export function payWeekEnd(start: ISODate): ISODate {
	return addDays(start, 6);
}

/** "Fri Oct 23" */
export function shortDay(iso: ISODate): string {
	const d = parseISODate(iso);
	return `${WEEKDAYS_SHORT[d.getDay()]} ${MONTHS_SHORT[d.getMonth()]} ${d.getDate()}`;
}

/**
 * "Fri Oct 23 – Thu Oct 29"; across a new year both ends carry their year
 * ("Fri Dec 25, 2026 – Thu Dec 31, 2026" never happens, "Fri Dec 31, 2027 – Thu Jan 6, 2028" does).
 */
export function planRangeLabel(start: ISODate, end: ISODate): string {
	const a = parseISODate(start);
	const b = parseISODate(end);
	if (a.getFullYear() !== b.getFullYear())
		return `${shortDay(start)}, ${a.getFullYear()} – ${shortDay(end)}, ${b.getFullYear()}`;
	return `${shortDay(start)} – ${shortDay(end)}`;
}

/** The receipt's bottom line: what is projected to be left for savings on the last day of the pay week. */
export const PROJECTED_LABEL = 'Projected Savings Amount';

/** "Left on Thursday": named for the last day of the pay week. */
export function leftLabel(end: ISODate): string {
	return `Left on ${weekdayName(weekday(end))}`;
}

/** "Bills before next Friday": named for the next payday. */
export function billsLabel(nextPayday: ISODate): string {
	return `Bills before next ${weekdayName(weekday(nextPayday))}`;
}

/** What the Copy button puts on the clipboard: digits only, the way a bank app wants it ("253.17"). */
export function copyText(cents: number): string {
	const safe = Number.isFinite(cents) ? Math.round(cents) : 0;
	const abs = Math.abs(safe);
	const text = `${Math.floor(abs / 100)}.${String(abs % 100).padStart(2, '0')}`;
	return safe < 0 ? `-${text}` : text;
}

/** "1st", "2nd", "15th", "22nd", "31st". */
export function ordinal(n: number): string {
	const v = n % 100;
	if (v >= 11 && v <= 13) return `${n}th`;
	return `${n}${['th', 'st', 'nd', 'rd'][n % 10] ?? 'th'}`;
}

/** "the 15th" for a date. */
export function onThe(iso: ISODate): string {
	return `the ${ordinal(parseISODate(iso).getDate())}`;
}

/** Strip label: the bills account by its own name, then "Savings", "Fun (Travis)". */
export function accountLabel(a: Pick<PlanAccount, 'name' | 'role' | 'ownerName'>): string {
	switch (a.role) {
		case 'savings':
			return 'Savings';
		case 'fun':
			return `Fun (${firstName(a.ownerName) ?? a.name})`;
		default:
			return a.name;
	}
}

/** "Travis Huffman" → "Travis"; null stays null. */
export function firstName(name: string | null | undefined): string | null {
	const n = (name ?? '').trim();
	return n ? n.split(/\s+/)[0] : null;
}

const ROLE_ORDER: Record<AccountRole, number> = { bills: 0, savings: 1, fun: 2, other: 3 };
/** Bills · Savings · Fun (by owner) · Other, the order of her note. */
export function orderAccounts<T extends Pick<PlanAccount, 'role' | 'name' | 'ownerName'>>(
	list: T[]
): T[] {
	return list.toSorted(
		(a, b) =>
			ROLE_ORDER[a.role] - ROLE_ORDER[b.role] ||
			(a.ownerName ?? '').localeCompare(b.ownerName ?? '') ||
			a.name.localeCompare(b.name)
	);
}

/**
 * The sub-line under "available now": with the bank's available balance pending is already in it
 * ("after 3 pending · −$62.40"); without it the app subtracts pending itself ("balance − pending").
 */
export function pendingLine(
	b: Pick<PlanBillsAccount, 'availableFromBank' | 'pending' | 'pendingOutCents' | 'balanceCents'> &
		Partial<Pick<PlanBillsAccount, 'addedCents'>>
): string {
	const added = b.pending.filter((p) => p?.source === 'added').length;
	const n = b.pending.length - added;
	// transactions added by hand aren't in the bank's numbers: they come off on top ("1 you added −$4.25")
	const extra = added ? ` · ${added} you added ${signedMoney(b.addedCents ?? 0)}` : '';
	if (b.availableFromBank) {
		if (n === 0) return added ? extra.slice(3) : 'nothing pending';
		return `after ${n} pending · ${signedMoney(-b.pendingOutCents)}${extra}`;
	}
	return `balance ${formatMoney(b.balanceCents)} − pending ${signedMoney(-b.pendingOutCents)}${extra}`;
}

export type ReceiptLine = {
	key: 'available' | 'paid' | 'bills' | 'fun' | 'oneoffs' | 'income';
	/** the operator in front of the label, like her note: "", "−", "+" */
	op: '' | '−' | '+';
	label: string;
	/** signed: outflows negative */
	cents: number;
	lines: PlanLine[];
};

/**
 * The calculation, top to bottom. Bills always show (it's the line she checks); fun money and
 * one-offs only when there are some; "+ Paycheck" only when one still lands before the end.
 */
export function receiptLines(plan: Plan): ReceiptLine[] {
	const b = plan.billsAccount;
	if (!b) return [];
	const out: ReceiptLine[] = [
		{
			key: 'available',
			op: '',
			label: `${b.name}, available now`,
			cents: b.availableCents,
			lines: []
		},
		...(plan.paidThisWeekCents
			? [
					<ReceiptLine>{
						key: 'paid',
						op: '+',
						label: 'Already paid this week',
						cents: plan.paidThisWeekCents,
						lines: [...plan.bills, ...plan.funMoney, ...plan.oneOffs].filter(
							(l) => l.occurrence.paid?.source === 'bank'
						)
					}
				]
			: []),
		{
			key: 'bills',
			op: '−',
			label: billsLabel(plan.nextPayday),
			cents: -plan.billsOutCents,
			lines: plan.bills
		}
	];
	if (plan.funMoney.length || plan.funOutCents)
		out.push({
			key: 'fun',
			op: '−',
			label: 'Fun money',
			cents: -plan.funOutCents,
			lines: plan.funMoney
		});
	if (plan.oneOffs.length || plan.oneOffOutCents)
		out.push({
			key: 'oneoffs',
			op: '−',
			label: 'One-offs',
			cents: -plan.oneOffOutCents,
			lines: plan.oneOffs
		});
	if (plan.incomeInCents > 0)
		out.push({
			key: 'income',
			op: '+',
			label: plan.income.length === 1 ? plan.income[0].occurrence.name : 'Paycheck',
			cents: plan.incomeInCents,
			lines: plan.income
		});
	return out;
}

/** The whole note as plain text (for "Copy the note"): one line per row, the result last. */
export function noteText(plan: Plan): string {
	const rows = receiptLines(plan).map(
		(l) => `${l.op ? `${l.op} ` : ''}${l.label}  ${signedOrBalance(l)}`
	);
	return [...rows, `= ${PROJECTED_LABEL}  ${formatMoney(plan.leftAtEndCents)}`].join('\n');
}
const signedOrBalance = (l: ReceiptLine) =>
	l.key === 'available' ? formatMoney(l.cents) : signedMoney(l.cents);

/** "From savings: Student loan −$312.00 on the 15th" */
export function savingsNote(line: PlanLine): string {
	const o = line.occurrence;
	return `${o.name} ${signedMoney(-o.amountCents)} on ${onThe(o.dueDate)}`;
}

/** Checklist progress; an empty list counts as done. */
export function checklistProgress(items: Pick<ChecklistItem, 'done'>[]): {
	done: number;
	total: number;
	allDone: boolean;
} {
	const done = items.filter((i) => i.done).length;
	return { done, total: items.length, allDone: done === items.length };
}

/** "Move $253.17 to savings", or "Nothing to move this week (−$42.10)" for a negative leftover. */
export function sweepText(plannedCents: number): string {
	if (plannedCents <= 0)
		return plannedCents === 0
			? 'Nothing to move this week'
			: `Nothing to move this week (${signedMoney(plannedCents)})`;
	return `Move ${formatMoney(plannedCents)} to savings`;
}

/**
 * The plan's bottom line: what is left on the last day of the pay week moves to savings that day.
 * `later` — the current week before its sweep day (the amount is today's Left on <day>);
 * `due` — frozen on the sweep day, not moved yet; `nothing` — frozen at zero (the week ended
 * negative); `done` — moved (by the bank, `auto`, or ticked by hand).
 */
export type SweepState =
	| { kind: 'none' }
	| { kind: 'later'; cents: number; day: string }
	| { kind: 'due'; cents: number; weekStart: string; today: boolean }
	| { kind: 'nothing'; leftCents: number }
	| { kind: 'done'; cents: number; auto: boolean; weekStart: string };

export function sweepState(
	plan: Pick<Plan, 'sweep' | 'isCurrent' | 'billsAccount' | 'leftAtEndCents' | 'end' | 'today'>
): SweepState {
	const s = plan.sweep;
	if (s?.doneAt)
		return {
			kind: 'done',
			cents: s.actualCents ?? s.plannedCents,
			auto: s.auto,
			weekStart: s.weekStart
		};
	if (s && s.plannedCents > 0)
		return {
			kind: 'due',
			cents: s.plannedCents,
			weekStart: s.weekStart,
			today: plan.today === plan.end
		};
	if (s) return { kind: 'nothing', leftCents: s.leftCents ?? 0 };
	if (plan.isCurrent && plan.billsAccount)
		return {
			kind: 'later',
			cents: Math.max(0, plan.leftAtEndCents),
			day: weekdayName(weekday(plan.end))
		};
	return { kind: 'none' };
}

/** True when the pay week starting on `start` holds the first payday of its month. */
export function isFirstPaydayOfMonth(start: ISODate): boolean {
	return parseISODate(start).getDate() <= 7;
}

/** A line's name in the expanded list: transfers drop the "Fun money · " the row already says. */
export function lineName(line: PlanLine): string {
	const n = line.occurrence.name;
	return line.kind === 'transfer' ? n.replace(/^fun money\s*·\s*/i, '') || n : n;
}

/**
 * Ticked by hand or by autopay, and the bank hasn't shown it yet: the money is still in the account, so the line still
 * counts in the plan until its bank transaction arrives (which then takes the tick over).
 */
export function waitingForBank(line: PlanLine): boolean {
	const p = line.occurrence.paid;
	return !!p && p.source !== 'bank';
}

/** Status word for a planned line: Paid / Late / Planned. */
export function lineStatus(line: PlanLine, today: ISODate): 'Paid' | 'Late' | 'Planned' {
	const o = line.occurrence;
	if (o.paid) return 'Paid';
	return o.dueDate < today ? 'Late' : 'Planned';
}

/**
 * Settings → Banks pending diagnostics: "sent 1 pending, 47 posted · available balance reported".
 * null before the first v7 sync.
 */
/** SimpleFIN refreshes a bank about once a day: older than this, the plan says how old the bank's numbers are. */
export const BANK_DATA_STALE_HOURS = 20;

/**
 * "Bank data as of Oct 26, 6:14 AM" (SimpleFIN's `balance-date`), and whether it is old enough to say so on the plan.
 * Null without a date.
 */
export function bankDataAge(
	balanceAt: string | null | undefined,
	now: Date = new Date()
): { label: string; stale: boolean } | null {
	if (!balanceAt) return null;
	const at = new Date(balanceAt);
	if (Number.isNaN(at.getTime())) return null;
	const hours = (now.getTime() - at.getTime()) / 3_600_000;
	return {
		label: `Bank data as of ${formatTimestamp(balanceAt)}`,
		stale: hours > BANK_DATA_STALE_HOURS
	};
}

export function feedSummary(
	feed: { pending: number; posted: number; availableReported: boolean } | null | undefined
): string | null {
	if (!feed) return null;
	const p = (n: number, w: string) => `${n} ${w}`;
	return `sent ${p(feed.pending, 'pending')}, ${p(feed.posted, 'posted')} · available balance ${
		feed.availableReported ? 'reported' : 'not reported'
	}`;
}

/**
 * What the numbers mean for her: fewer pending in the app than the feed sent is our bug; a feed
 * that sends fewer than the bank app shows is the bank's feed, and the available balance still
 * keeps the weekly number right (when the bank reports it).
 */
export function feedAdvice(
	feed: { pending: number; availableReported: boolean } | null | undefined,
	shownPending: number | null
): string | null {
	if (!feed) return null;
	if (shownPending !== null && shownPending < feed.pending)
		return `The bank sent ${feed.pending} pending but we show ${shownPending}. That's on us: re-check matches, and if it stays, tell us.`;
	if (feed.availableReported)
		return 'If your bank app shows more pending than this, the bank feed is holding them back. The available balance already includes them, so the weekly number is still right.';
	return "If your bank app shows more pending than this, the bank feed is holding them back. This bank doesn't report an available balance, so those charges aren't counted until they arrive.";
}

/**
 * A checklist line as shown: the API's label ("Paycheck arrived · $2,450.00", "$20.00 → Ally
 * Spending (Travis)"), with the amount on the right only when the label doesn't already say it.
 */
export function checklistText(item: Pick<ChecklistItem, 'key' | 'label' | 'amountCents'>): {
	text: string;
	showAmount: boolean;
} {
	const label = item.label.trim() || (item.key === 'paycheck' ? 'Paycheck arrived' : 'Fun money');
	return { text: label, showAmount: !/\$\s?\d/.test(label) };
}
