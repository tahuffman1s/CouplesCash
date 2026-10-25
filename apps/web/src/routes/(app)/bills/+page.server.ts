import type { PageServerLoad } from './$types';
import type { CalendarResponse, Category, Expense, RecurringItem } from '#lib/types.ts';
import { api, endpoints } from '#lib/server/api.ts';
import { addDays, today } from '#lib/utils/dates.ts';

export const load: PageServerLoad = async (event) => {
	const m = event.url.searchParams.get('m');
	const month = m && /^\d{4}-(0[1-9]|1[0-2])$/.test(m) ? m : today().slice(0, 7);
	if (!event.locals.household)
		return {
			items: null,
			categories: [] as Category[],
			expenses: [] as Expense[],
			month,
			oneOffPaid: [] as string[]
		};
	const client = api(event);
	const [items, categories, expenses] = await Promise.all([
		client.get<RecurringItem[]>('/recurring'),
		client.get<Category[]>('/categories'),
		endpoints(event)
			.expenses.list(month)
			.catch(() => [] as Expense[])
	]);
	// v4: which one-offs have cleared (their single occurrence is paid), for the One-offs chip
	const onceDates = items.filter((i) => i.frequency === 'once').map((i) => i.anchorDate);
	let oneOffPaid: string[] = [];
	if (onceDates.length) {
		const from = onceDates.reduce((a, b) => (a < b ? a : b), addDays(today(), -366));
		const to = onceDates.reduce((a, b) => (a > b ? a : b), today());
		oneOffPaid = await client
			.get<CalendarResponse>(`/calendar?from=${from}&to=${to}`)
			.then((c) => c.occurrences.filter((o) => o.paid).map((o) => o.itemId))
			.catch(() => [] as string[]);
	}
	return {
		items,
		categories,
		expenses,
		month,
		tab: event.url.searchParams.get('tab'),
		oneOffPaid
	};
};
