import type { PageServerLoad } from './$types';
import type { BudgetsResponse, RecurringItem, Summary, WeekBudget } from '#lib/types.ts';
import { api, endpoints } from '#lib/server/api.ts';
import { today } from '#lib/utils/dates.ts';

export const load: PageServerLoad = async (event) => {
	const m = event.url.searchParams.get('m');
	const month = m && /^\d{4}-(0[1-9]|1[0-2])$/.test(m) ? m : today().slice(0, 7);
	const period: 'monthly' | 'weekly' =
		event.url.searchParams.get('period') === 'weekly' ? 'weekly' : 'monthly';
	if (!event.locals.household)
		return {
			month,
			period,
			summary: null,
			subscriptions: [] as RecurringItem[],
			budgets: null,
			weekBudget: null as WeekBudget | null
		};
	const client = api(event);
	const v2 = endpoints(event);
	const [summary, subscriptions, budgets, weekBudget] = await Promise.all([
		client.get<Summary>(`/summary?month=${month}`),
		client.get<RecurringItem[]>('/recurring?kind=subscription'),
		v2.budgets.list(month).catch(() => null as BudgetsResponse | null),
		// v4: the current week's rows for the Weekly segment
		v2.budgets.week().catch(() => null as WeekBudget | null)
	]);
	return { month, period, summary, subscriptions, budgets, weekBudget };
};
