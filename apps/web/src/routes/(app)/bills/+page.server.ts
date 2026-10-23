import type { PageServerLoad } from './$types';
import type { Category, Expense, RecurringItem } from '#lib/types.ts';
import { api, endpoints } from '#lib/server/api.ts';
import { today } from '#lib/utils/dates.ts';

export const load: PageServerLoad = async (event) => {
	const m = event.url.searchParams.get('m');
	const month = m && /^\d{4}-(0[1-9]|1[0-2])$/.test(m) ? m : today().slice(0, 7);
	if (!event.locals.household)
		return { items: null, categories: [] as Category[], expenses: [] as Expense[], month };
	const client = api(event);
	const [items, categories, expenses] = await Promise.all([
		client.get<RecurringItem[]>('/recurring'),
		client.get<Category[]>('/categories'),
		endpoints(event)
			.expenses.list(month)
			.catch(() => [] as Expense[])
	]);
	return { items, categories, expenses, month, tab: event.url.searchParams.get('tab') };
};
