import { describe, expect, it } from 'vitest';
import type { Occurrence, Plan, PlanLine } from '#lib/types.ts';
import {
	accountLabel,
	billsLabel,
	checklistProgress,
	checklistText,
	copyText,
	feedAdvice,
	feedSummary,
	firstName,
	isFirstPaydayOfMonth,
	leftLabel,
	lineName,
	lineStatus,
	waitingForBank,
	noteText,
	onThe,
	orderAccounts,
	ordinal,
	payWeekEnd,
	payWeekStart,
	pendingLine,
	planRangeLabel,
	receiptLines,
	savingsNote,
	shortDay,
	sweepText,
	weekdayName
} from './plan.ts';

const occ = (over: Partial<Occurrence> = {}): Occurrence => ({
	itemId: 'i1',
	name: 'Rent',
	kind: 'bill',
	amountCents: 185000,
	dueDate: '2026-10-27',
	autoPay: false,
	categoryId: null,
	categoryName: null,
	categoryColor: null,
	accountId: null,
	accountName: null,
	paid: null,
	varianceCents: null,
	status: 'planned',
	...over
});
const line = (over: Partial<Occurrence> = {}, kind: PlanLine['kind'] = 'bill'): PlanLine => ({
	kind,
	occurrence: occ(over)
});

function plan(over: Partial<Plan> = {}): Plan {
	return {
		start: '2026-10-23',
		end: '2026-10-29',
		nextPayday: '2026-10-30',
		isCurrent: true,
		isPayday: false,
		today: '2026-10-26',
		billsAccount: {
			id: 'a1',
			name: 'US Bank',
			availableCents: 234567,
			balanceCents: 240807,
			availableFromBank: true,
			pending: [],
			pendingOutCents: 6240,
			feed: null
		},
		accounts: [],
		bills: [line()],
		funMoney: [],
		oneOffs: [],
		income: [],
		billsOutCents: 199250,
		funOutCents: 0,
		oneOffOutCents: 0,
		incomeInCents: 0,
		sweepOutCents: 0,
		leftAtEndCents: 35317,
		savingsBills: [],
		checklist: [],
		sweep: null,
		toReviewCount: 0,
		setup: {
			banksLinked: true,
			rolesSet: true,
			paycheckKnown: true,
			billsAdded: true,
			partnerJoined: true
		},
		...over
	};
}

describe('weekdayName', () => {
	it('names every day and wraps bad input', () => {
		expect(weekdayName(5)).toBe('Friday');
		expect(weekdayName(4)).toBe('Thursday');
		expect(weekdayName(0, 'short')).toBe('Sun');
		expect(weekdayName(7)).toBe('Sunday');
		expect(weekdayName(-1)).toBe('Saturday');
		expect(weekdayName(Number.NaN)).toBe('Sunday');
	});
});

describe('pay week bounds', () => {
	it('starts on the most recent payday (today counts)', () => {
		expect(payWeekStart('2026-10-26', 5)).toBe('2026-10-23'); // Mon → last Fri
		expect(payWeekStart('2026-10-23', 5)).toBe('2026-10-23'); // payday itself
		expect(payWeekStart('2026-10-29', 5)).toBe('2026-10-23'); // Thu, last day
		expect(payWeekStart('2026-10-30', 5)).toBe('2026-10-30');
	});
	it('works for any payday, across months and years', () => {
		expect(payWeekStart('2026-10-26', 1)).toBe('2026-10-26'); // Monday payday
		expect(payWeekStart('2026-10-26', 2)).toBe('2026-10-20'); // Tuesday payday
		expect(payWeekStart('2026-11-02', 5)).toBe('2026-10-30');
		expect(payWeekStart('2027-01-01', 5)).toBe('2027-01-01');
		expect(payWeekStart('2027-01-03', 5)).toBe('2027-01-01');
		expect(payWeekEnd('2026-10-23')).toBe('2026-10-29');
		expect(payWeekEnd('2026-12-31')).toBe('2027-01-06');
	});
});

describe('labels', () => {
	it('reads like her note, from the payday weekday', () => {
		expect(shortDay('2026-10-23')).toBe('Fri Oct 23');
		expect(planRangeLabel('2026-10-23', '2026-10-29')).toBe('Fri Oct 23 – Thu Oct 29');
		expect(planRangeLabel('2026-10-30', '2026-11-05')).toBe('Fri Oct 30 – Thu Nov 5');
		expect(planRangeLabel('2027-12-31', '2028-01-06')).toBe('Fri Dec 31, 2027 – Thu Jan 6, 2028');
		expect(leftLabel('2026-10-29')).toBe('Left on Thursday');
		expect(billsLabel('2026-10-30')).toBe('Bills before next Friday');
		// a Monday payday: the week ends on Sunday
		expect(leftLabel('2026-11-01')).toBe('Left on Sunday');
		expect(billsLabel('2026-11-02')).toBe('Bills before next Monday');
	});
	it('ordinals', () => {
		expect([1, 2, 3, 4, 11, 12, 13, 15, 21, 22, 23, 31].map(ordinal)).toEqual([
			'1st',
			'2nd',
			'3rd',
			'4th',
			'11th',
			'12th',
			'13th',
			'15th',
			'21st',
			'22nd',
			'23rd',
			'31st'
		]);
		expect(onThe('2026-11-15')).toBe('the 15th');
	});
});

describe('copyText', () => {
	it('digits only, two decimals', () => {
		expect(copyText(25317)).toBe('253.17');
		expect(copyText(123456789)).toBe('1234567.89');
		expect(copyText(5)).toBe('0.05');
		expect(copyText(0)).toBe('0.00');
		expect(copyText(-4210)).toBe('-42.10');
		expect(copyText(100.4)).toBe('1.00');
		expect(copyText(Number.NaN)).toBe('0.00');
	});
});

describe('accounts', () => {
	it('labels the strip', () => {
		expect(accountLabel({ name: 'US Bank', role: 'bills', ownerName: null })).toBe('US Bank');
		expect(accountLabel({ name: 'Ally HYS', role: 'savings', ownerName: null })).toBe('Savings');
		expect(accountLabel({ name: 'Spending', role: 'fun', ownerName: 'Travis Huffman' })).toBe(
			'Fun (Travis)'
		);
		expect(accountLabel({ name: 'Spending', role: 'fun', ownerName: null })).toBe('Fun (Spending)');
		expect(accountLabel({ name: 'Visa', role: 'other', ownerName: null })).toBe('Visa');
		expect(firstName('  Jess  Huffman ')).toBe('Jess');
		expect(firstName('')).toBeNull();
	});
	it('orders bills, savings, fun by owner, other', () => {
		const list = [
			{ name: 'Visa', role: 'other' as const, ownerName: null },
			{ name: 'Fun B', role: 'fun' as const, ownerName: 'Travis' },
			{ name: 'Ally', role: 'savings' as const, ownerName: null },
			{ name: 'Fun A', role: 'fun' as const, ownerName: 'Jess' },
			{ name: 'US Bank', role: 'bills' as const, ownerName: null }
		];
		expect(orderAccounts(list).map((a) => a.name)).toEqual([
			'US Bank',
			'Ally',
			'Fun A',
			'Fun B',
			'Visa'
		]);
	});
});

describe('pendingLine', () => {
	const pending = [{}, {}, {}] as never[];
	it('available from the bank: pending is already in it', () => {
		expect(
			pendingLine({ availableFromBank: true, pending, pendingOutCents: 6240, balanceCents: 0 })
		).toBe('after 3 pending · −$62.40');
		expect(
			pendingLine({ availableFromBank: true, pending: [], pendingOutCents: 0, balanceCents: 0 })
		).toBe('nothing pending');
	});
	it('without it: balance − pending', () => {
		expect(
			pendingLine({
				availableFromBank: false,
				pending,
				pendingOutCents: 6240,
				balanceCents: 240807
			})
		).toBe('balance $2,408.07 − pending −$62.40');
	});
});

describe('receiptLines', () => {
	it('available and bills always, the rest only when there is some', () => {
		const lines = receiptLines(plan());
		expect(lines.map((l) => [l.op, l.label, l.cents])).toEqual([
			['', 'US Bank, available now', 234567],
			['−', 'Bills before next Friday', -199250]
		]);
	});
	it('fun money, one-offs and a paycheck still to land', () => {
		const lines = receiptLines(
			plan({
				funMoney: [line({ name: 'Fun money · Travis', amountCents: 2000 }, 'transfer')],
				funOutCents: 4000,
				oneOffs: [line({ name: 'Oil change', amountCents: 6000 }, 'oneoff')],
				oneOffOutCents: 6000,
				income: [line({ name: 'Paycheck', kind: 'income', amountCents: 245000 }, 'income')],
				incomeInCents: 245000
			})
		);
		expect(lines.map((l) => l.key)).toEqual(['available', 'bills', 'fun', 'oneoffs', 'income']);
		expect(lines[2]).toMatchObject({ op: '−', label: 'Fun money', cents: -4000 });
		expect(lines[3]).toMatchObject({ op: '−', label: 'One-offs', cents: -6000 });
		expect(lines[4]).toMatchObject({ op: '+', label: 'Paycheck', cents: 245000 });
	});
	it("last week's leftover still to move comes right after available", () => {
		const lines = receiptLines(plan({ sweepOutCents: 84567 }));
		expect(lines.map((l) => l.key)).toEqual(['available', 'sweep', 'bills']);
		expect(lines[1]).toMatchObject({ op: '−', label: 'Move to savings', cents: -84567 });
	});
	it('no paycheck line once it has arrived; nothing without a bills account', () => {
		const lines = receiptLines(
			plan({ income: [line({ kind: 'income', paid: null }, 'income')], incomeInCents: 0 })
		);
		expect(lines.some((l) => l.key === 'income')).toBe(false);
		expect(receiptLines(plan({ billsAccount: null }))).toEqual([]);
	});
	it('the whole note as text', () => {
		expect(noteText(plan())).toBe(
			[
				'US Bank, available now  $2,345.67',
				'− Bills before next Friday  −$1,992.50',
				'= Left on Thursday  $353.17'
			].join('\n')
		);
		expect(noteText(plan({ leftAtEndCents: -4210 }))).toContain('= Left on Thursday  -$42.10');
	});
});

describe('checklist + sweep', () => {
	it('progress', () => {
		expect(checklistProgress([{ done: true }, { done: false }])).toEqual({
			done: 1,
			total: 2,
			allDone: false
		});
		expect(checklistProgress([{ done: true }])).toMatchObject({ allDone: true });
		expect(checklistProgress([])).toMatchObject({ allDone: true, total: 0 });
	});
	it('sweep wording', () => {
		expect(sweepText(25317)).toBe('Move $253.17 to savings');
		expect(sweepText(-4210)).toBe('Nothing to move this week (−$42.10)');
		expect(sweepText(0)).toBe('Nothing to move this week');
	});
	it('first payday of the month', () => {
		expect(isFirstPaydayOfMonth('2026-11-06')).toBe(true);
		expect(isFirstPaydayOfMonth('2026-11-01')).toBe(true);
		expect(isFirstPaydayOfMonth('2026-11-07')).toBe(true);
		expect(isFirstPaydayOfMonth('2026-11-08')).toBe(false);
		expect(isFirstPaydayOfMonth('2026-10-30')).toBe(false);
	});
	it('savings note and line status', () => {
		expect(
			savingsNote(line({ name: 'Student loan', amountCents: 31200, dueDate: '2026-11-15' }))
		).toBe('Student loan −$312.00 on the 15th');
		expect(lineStatus(line({ dueDate: '2026-10-27' }), '2026-10-26')).toBe('Planned');
		expect(lineStatus(line({ dueDate: '2026-10-24' }), '2026-10-26')).toBe('Late');
		expect(
			lineStatus(
				line({
					paid: { paidAt: '', paidBy: null, amountCents: 1, auto: true }
				}),
				'2026-10-26'
			)
		).toBe('Paid');
	});
	it('a tick by hand or by autopay waits for the bank; a bank payment does not', () => {
		const paid = (source: 'manual' | 'autopay' | 'bank' | undefined) =>
			line({ paid: { paidAt: '', paidBy: null, amountCents: 1, auto: false, source } });
		expect(waitingForBank(paid('manual'))).toBe(true);
		expect(waitingForBank(paid('autopay'))).toBe(true);
		expect(waitingForBank(paid(undefined))).toBe(true);
		expect(waitingForBank(paid('bank'))).toBe(false);
		expect(waitingForBank(line())).toBe(false);
	});
});

describe('feed diagnostics', () => {
	it('summary', () => {
		expect(feedSummary({ pending: 1, posted: 47, availableReported: true })).toBe(
			'sent 1 pending, 47 posted · available balance reported'
		);
		expect(feedSummary({ pending: 0, posted: 3, availableReported: false })).toBe(
			'sent 0 pending, 3 posted · available balance not reported'
		);
		expect(feedSummary(null)).toBeNull();
	});
	it('advice', () => {
		expect(feedAdvice({ pending: 3, availableReported: true }, 1)).toMatch(/That's on us/);
		expect(feedAdvice({ pending: 1, availableReported: true }, 1)).toMatch(
			/available balance already includes them/
		);
		expect(feedAdvice({ pending: 1, availableReported: false }, null)).toMatch(
			/doesn't report an available balance/
		);
		expect(feedAdvice(null, 0)).toBeNull();
	});
});

describe('lineName', () => {
	it('drops the "Fun money · " prefix on transfers only', () => {
		expect(lineName(line({ name: 'Fun money · Jess (monthly)' }, 'transfer'))).toBe(
			'Jess (monthly)'
		);
		expect(lineName(line({ name: 'Fun money · Travis' }, 'transfer'))).toBe('Travis');
		expect(lineName(line({ name: 'Fun money' }, 'transfer'))).toBe('Fun money');
		expect(lineName(line({ name: 'Fun money · x' }, 'bill'))).toBe('Fun money · x');
	});
});

describe('checklistText', () => {
	it('keeps the API label and never repeats its amount', () => {
		expect(
			checklistText({ key: 'paycheck', label: 'Paycheck arrived · $2,450.00', amountCents: 245000 })
		).toEqual({ text: 'Paycheck arrived · $2,450.00', showAmount: false });
		expect(
			checklistText({ key: 'fun', label: '$20.00 → Ally Spending (Travis)', amountCents: 2000 })
		).toEqual({ text: '$20.00 → Ally Spending (Travis)', showAmount: false });
		expect(checklistText({ key: 'paycheck', label: 'Paycheck arrived', amountCents: 1 })).toEqual({
			text: 'Paycheck arrived',
			showAmount: true
		});
		expect(checklistText({ key: 'fun', label: '', amountCents: 1 }).text).toBe('Fun money');
	});
	it('the sweep', () => {
		expect(checklistText({ key: 'sweep', label: 'x', amountCents: 25317 }).text).toBe(
			'Move $253.17 to savings'
		);
		expect(
			checklistText({ key: 'sweep', label: 'Nothing to move this week (−$42.10)', amountCents: 0 })
				.text
		).toBe('Nothing to move this week (−$42.10)');
		expect(checklistText({ key: 'sweep', label: '', amountCents: 0 }).text).toBe(
			'Nothing to move this week'
		);
	});
});
