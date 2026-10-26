import { fail } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type {
	Account,
	BillSuggestion,
	Category,
	Household,
	PendingView,
	RecurringItem,
	WeekView
} from '#lib/types.ts';
import { api, endpoints, isApiError } from '#lib/server/api.ts';
import { echo, parseWeeklyBudgetsForm } from '#lib/server/forms.ts';
import { quickAddAction } from '#lib/server/quickadd.ts';
import { isValidISODate, today } from '#lib/utils/dates.ts';
import { nextPayday } from '#lib/utils/payperiod.ts';

/**
 * v6 home. Besides the week itself: whatever the first-run checklist, the found-bills card, the
 * pending markers and the "Next payday" line need. Everything but the week degrades to empty.
 */
export const load: PageServerLoad = async (event) => {
	const s = event.url.searchParams.get('start');
	const start = s && isValidISODate(s) ? s : null;
	const empty = {
		start,
		view: null as WeekView | null,
		categories: [] as Category[],
		accounts: [] as Account[],
		itemCount: 0,
		suggestions: [] as BillSuggestion[],
		pending: null as PendingView | null,
		payday: null as { date: string; amountCents: number } | null,
		joined: event.url.searchParams.has('joined')
	};
	if (!event.locals.household) return empty;
	const client = api(event);
	const v = endpoints(event);
	const quiet = <T>(p: Promise<T>, fallback: T) => p.catch(() => fallback);
	const [view, categories, accounts, items, suggestions, pending, household] = await Promise.all([
		v.week.view(start),
		quiet(client.get<Category[]>('/categories'), [] as Category[]),
		quiet(client.get<Account[]>('/accounts'), [] as Account[]),
		quiet(v.recurring.list(), [] as RecurringItem[]),
		quiet(v.billSuggestions.list(), [] as BillSuggestion[]),
		quiet(v.pending.view(), null as PendingView | null),
		quiet(v.household.get(), null as Household | null)
	]);
	// "Next payday Fri Nov 6 · $3,200": the income expected in the period that payday starts
	let payday: { date: string; amountCents: number } | null = null;
	if (household?.payAnchorDate && view.isCurrent) {
		const date = nextPayday(household.payPeriod, household.payAnchorDate, view.today ?? today());
		const next = await quiet(v.payperiod.view(date), null);
		payday = { date, amountCents: next?.expectedIncomeCents ?? 0 };
	}
	return {
		...empty,
		view,
		categories,
		accounts,
		itemCount: items.length,
		suggestions,
		pending,
		payday
	};
};

export const actions: Actions = {
	quickAdd: quickAddAction,
	/** "Set weekly budgets": the overall target (PATCH /couple) + one PUT/DELETE per category. */
	budgets: async (event) => {
		const data = await event.request.formData();
		const parsed = parseWeeklyBudgetsForm(data);
		if (!parsed.ok) return fail(400, { sheet: 'budgets', error: parsed.error, values: echo(data) });
		const v2 = endpoints(event);
		try {
			await v2.household.update({ weeklySpendCents: parsed.value.overallCents });
			for (const c of parsed.value.categories) {
				if (c.amountCents !== null) await v2.budgets.setWeekly(c.categoryId, c.amountCents);
				else if (c.had) await v2.budgets.clearWeekly(c.categoryId).catch(() => undefined);
			}
			return { budgetsSaved: true };
		} catch (e) {
			const message = isApiError(e) ? e.message : 'Could not save the budgets. Try again.';
			return fail(isApiError(e) ? e.status : 500, {
				sheet: 'budgets',
				error: message,
				values: echo(data)
			});
		}
	}
};
