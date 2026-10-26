/**
 * v7 first-run setup helpers (pure, unit-tested): the six steps, the fun-money transfer items
 * ($20 a week and $150 a month each by default), and spotting the paycheck in bank deposits.
 */
import type { InboxItem, PlanSetup, RecurringItem } from '#lib/types.ts';
import { addDays, addMonths, startOfMonth, weekday, type ISODate } from './dates.ts';
import { firstName } from './plan.ts';

export const SETUP_STEPS = [
	{ key: 'banks', title: 'Link your banks', hint: 'So the numbers fill themselves in' },
	{ key: 'roles', title: 'Tell us which account is which', hint: 'Bills, savings and fun money' },
	{ key: 'payday', title: 'Payday', hint: 'The day your week starts' },
	{ key: 'fun', title: 'Fun money', hint: 'What goes to each of you' },
	{ key: 'notes', title: 'Paste your notes', hint: 'Your bill list, as you have it' },
	{ key: 'partner', title: 'Invite your partner', hint: 'With a Couple Code' }
] as const;
export type SetupStep = (typeof SETUP_STEPS)[number]['key'];

export function isSetupStep(v: string | null | undefined): v is SetupStep {
	return SETUP_STEPS.some((s) => s.key === v);
}
export function nextStep(step: SetupStep): SetupStep | null {
	const i = SETUP_STEPS.findIndex((s) => s.key === step);
	return SETUP_STEPS[i + 1]?.key ?? null;
}
export function prevStep(step: SetupStep): SetupStep | null {
	const i = SETUP_STEPS.findIndex((s) => s.key === step);
	return i > 0 ? SETUP_STEPS[i - 1].key : null;
}

/** Which steps the server already sees as done (fun money: transfer items exist). */
export function stepsDone(
	setup: PlanSetup | null | undefined,
	extra: { funSet: boolean }
): Record<SetupStep, boolean> {
	return {
		banks: !!setup?.banksLinked,
		roles: !!setup?.rolesSet,
		payday: !!setup?.paycheckKnown,
		fun: extra.funSet,
		notes: !!setup?.billsAdded,
		partner: !!setup?.partnerJoined
	};
}

/** The first step not done yet (null when everything is). */
export function firstOpenStep(done: Record<SetupStep, boolean>): SetupStep | null {
	return SETUP_STEPS.find((s) => !done[s.key])?.key ?? null;
}

/** The next date (today counts) that falls on `wd`. */
export function nextWeekdayOnOrAfter(today: ISODate, wd: number): ISODate {
	return addDays(today, (wd - weekday(today) + 7) % 7);
}
/** The most recent date (today counts) that fell on `wd`. */
export function lastWeekdayOnOrBefore(today: ISODate, wd: number): ISODate {
	return addDays(today, -((weekday(today) - wd + 7) % 7));
}

export const DEFAULT_FUN_WEEKLY_CENTS = 2000;
export const DEFAULT_FUN_MONTHLY_CENTS = 15000;

/** One fun-money account in the form: its amounts and the items they came from. */
export type FunRow = {
	accountId: string;
	label: string;
	memberName: string;
	weeklyCents: number;
	monthlyCents: number;
	weeklyItemId: string | null;
	monthlyItemId: string | null;
};
export type FunPlan = {
	memberName: string;
	toAccountId: string;
	weeklyCents: number;
	monthlyCents: number;
};
export type TransferBody = {
	name: string;
	kind: 'transfer';
	amountCents: number;
	categoryId: null;
	accountId: string;
	fromAccountId: string;
	toAccountId: string;
	frequency: 'weekly' | 'monthly';
	anchorDate: string;
	endDate: null;
	autoPay: false;
	notes: string | null;
	url: null;
	bankNames: string[];
	amountVaries: false;
};

export const funItemName = (memberName: string, monthly: boolean) =>
	`Fun money · ${firstName(memberName) ?? memberName}${monthly ? ' (monthly)' : ''}`;

/**
 * The fun-money `transfer` items: weekly on payday (anchored on this pay week's payday) and monthly
 * on the 1st (anchored on this month's 1st). Zero amounts are left out.
 */
export function funItemBodies(
	plans: FunPlan[],
	opts: { billsAccountId: string; payday: ISODate; today: ISODate }
): TransferBody[] {
	const out: TransferBody[] = [];
	for (const p of plans) {
		const base = {
			kind: 'transfer' as const,
			categoryId: null,
			accountId: opts.billsAccountId,
			fromAccountId: opts.billsAccountId,
			toAccountId: p.toAccountId,
			endDate: null,
			autoPay: false as const,
			url: null,
			bankNames: [] as string[],
			amountVaries: false as const
		};
		if (p.weeklyCents > 0)
			out.push({
				...base,
				name: funItemName(p.memberName, false),
				amountCents: p.weeklyCents,
				frequency: 'weekly',
				anchorDate: opts.payday,
				notes: 'Fun money, every payday'
			});
		if (p.monthlyCents > 0)
			out.push({
				...base,
				name: funItemName(p.memberName, true),
				amountCents: p.monthlyCents,
				frequency: 'monthly',
				anchorDate: startOfMonth(opts.today),
				notes: 'Fun money, at the start of the month'
			});
	}
	return out;
}

/** The fun-money items already planned for one fun account (weekly + monthly). */
export function funItemsFor(items: RecurringItem[], toAccountId: string) {
	const mine = items.filter((i) => i.kind === 'transfer' && i.toAccountId === toAccountId);
	return {
		weekly: mine.find((i) => i.frequency === 'weekly') ?? null,
		monthly: mine.find((i) => i.frequency === 'monthly') ?? null
	};
}

const PAYROLL_RE = /payroll|direct dep|dir dep|salary|paycheck|pay ?check|wages|ach credit/i;

/** The latest posted deposit that looks like a paycheck, or null (never guesses from random deposits). */
export function detectPaycheck(
	rows: Pick<InboxItem, 'amountCents' | 'description' | 'payee' | 'pending' | 'postedOn'>[]
): { amountCents: number; bankName: string; postedOn: string } | null {
	const hit = rows
		.filter(
			(r) =>
				r.amountCents > 0 &&
				!r.pending &&
				(PAYROLL_RE.test(r.description) || (!!r.payee && PAYROLL_RE.test(r.payee)))
		)
		.toSorted((a, b) => b.postedOn.localeCompare(a.postedOn))[0];
	if (!hit) return null;
	return {
		amountCents: hit.amountCents,
		bankName: (hit.payee || hit.description).slice(0, 80),
		postedOn: hit.postedOn
	};
}

/** The 1st of next month, for "the next $150". */
export function nextFirstOfMonth(today: ISODate): ISODate {
	return addMonths(startOfMonth(today), 1);
}
