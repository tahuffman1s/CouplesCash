import type { PageServerLoad } from './$types';
import type { BudgetsResponse, RecurringItem, Summary } from '#lib/types.ts';
import { api, endpoints } from '#lib/server/api.ts';
import { today } from '#lib/utils/dates.ts';

export const load: PageServerLoad = async (event) => {
	const m = event.url.searchParams.get('m');
	const month = m && /^\d{4}-(0[1-9]|1[0-2])$/.test(m) ? m : today().slice(0, 7);
	if (!event.locals.household)
		return { month, summary: null, subscriptions: [] as RecurringItem[], budgets: null };
	const client = api(event);
	const [summary, subscriptions, budgets] = await Promise.all([
		client.get<Summary>(`/summary?month=${month}`),
		client.get<RecurringItem[]>('/recurring?kind=subscription'),
		endpoints(event)
			.budgets.list(month)
			.catch(() => null as BudgetsResponse | null)
	]);
	return { month, summary, subscriptions, budgets };
};
