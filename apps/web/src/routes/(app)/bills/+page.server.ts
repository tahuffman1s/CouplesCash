import { redirect } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type {
	Account,
	BillSuggestion,
	CalendarResponse,
	Category,
	Occurrence,
	RecurringItem
} from '#lib/types.ts';
import { api, endpoints } from '#lib/server/api.ts';
import { quickAddAction } from '#lib/server/quickadd.ts';
import { addDays, endOfMonth, startOfMonth, today } from '#lib/utils/dates.ts';

/** v6 Bills: everything that repeats, plus one-offs. List ⇄ Calendar (`?view=calendar&m=`). */
export const load: PageServerLoad = async (event) => {
	const q = event.url.searchParams;
	// v6: expenses live in Transactions now
	if (q.get('tab') === 'expenses') redirect(308, '/transactions');
	const m = q.get('m');
	const month = m && /^\d{4}-(0[1-9]|1[0-2])$/.test(m) ? m : today().slice(0, 7);
	const view: 'list' | 'calendar' = q.get('view') === 'calendar' ? 'calendar' : 'list';
	const d = q.get('d');
	const empty = {
		view,
		month,
		day: d && /^\d{4}-\d{2}-\d{2}$/.test(d) ? d : null,
		tab: q.get('tab'),
		items: null as RecurringItem[] | null,
		categories: [] as Category[],
		accounts: [] as Account[],
		suggestions: [] as BillSuggestion[],
		occurrences: [] as Occurrence[],
		oneOffPaid: [] as string[]
	};
	if (!event.locals.household) return empty;
	const client = api(event);
	const quiet = <T>(p: Promise<T>, fallback: T) => p.catch(() => fallback);
	const [items, categories, accounts, suggestions, calendar] = await Promise.all([
		client.get<RecurringItem[]>('/recurring'),
		quiet(client.get<Category[]>('/categories'), [] as Category[]),
		quiet(client.get<Account[]>('/accounts'), [] as Account[]),
		quiet(endpoints(event).billSuggestions.list(), [] as BillSuggestion[]),
		view === 'calendar'
			? client
					.get<CalendarResponse>(
						`/calendar?from=${startOfMonth(`${month}-01`)}&to=${endOfMonth(`${month}-01`)}`
					)
					.then((c) => c.occurrences)
			: Promise.resolve([] as Occurrence[])
	]);
	// which one-offs are paid (their single occurrence), for the One-offs chip
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
		...empty,
		items,
		categories,
		accounts,
		suggestions,
		occurrences: calendar,
		oneOffPaid
	};
};

export const actions: Actions = { quickAdd: quickAddAction };
