import { fail } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type { Account, Category, WeekView } from '#lib/types.ts';
import { api, endpoints, isApiError } from '#lib/server/api.ts';
import { echo, parseOneOffForm, parseWeeklyBudgetsForm } from '#lib/server/forms.ts';
import { isValidISODate } from '#lib/utils/dates.ts';

export const load: PageServerLoad = async (event) => {
	const s = event.url.searchParams.get('start');
	const start = s && isValidISODate(s) ? s : null;
	if (!event.locals.household)
		return {
			start,
			view: null as WeekView | null,
			categories: [] as Category[],
			accounts: [] as Account[]
		};
	const client = api(event);
	const [view, categories, accounts] = await Promise.all([
		endpoints(event).week.view(start),
		client.get<Category[]>('/categories').catch(() => [] as Category[]),
		client.get<Account[]>('/accounts').catch(() => [] as Account[])
	]);
	return { start, view, categories, accounts };
};

export const actions: Actions = {
	/** "Plan a one-off" → POST /recurring with frequency once. */
	planOneOff: async (event) => {
		const data = await event.request.formData();
		const parsed = parseOneOffForm(data);
		if (!parsed.ok) return fail(400, { sheet: 'oneoff', error: parsed.error, values: echo(data) });
		try {
			const item = await endpoints(event).recurring.create(parsed.value);
			return { oneOffAdded: item.name };
		} catch (e) {
			const message = isApiError(e) ? e.message : 'Could not save. Try again.';
			return fail(isApiError(e) ? e.status : 500, {
				sheet: 'oneoff',
				error: message,
				values: echo(data)
			});
		}
	},
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
