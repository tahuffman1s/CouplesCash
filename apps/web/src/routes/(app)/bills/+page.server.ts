import { redirect } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type {
	Account,
	BillSuggestion,
	CalendarResponse,
	Category,
	RecurringItem
} from '#lib/types.ts';
import { api, endpoints } from '#lib/server/api.ts';
import { quickAddAction } from '#lib/server/quickadd.ts';
import { addDays, today } from '#lib/utils/dates.ts';

/** v7 Bills: one list grouped by pay week (this pay week · next 4 weeks · later), found bills. */
export const load: PageServerLoad = async (event) => {
	const q = event.url.searchParams;
	// v6: expenses live in Transactions now
	if (q.get('tab') === 'expenses') redirect(308, '/transactions');
	const empty = {
		tab: q.get('tab'),
		added: Number(q.get('added') ?? 0) || 0,
		items: null as RecurringItem[] | null,
		categories: [] as Category[],
		accounts: [] as Account[],
		suggestions: [] as BillSuggestion[],
		oneOffPaid: [] as string[]
	};
	if (!event.locals.household) return empty;
	const client = api(event);
	const v = endpoints(event);
	const quiet = <T>(p: Promise<T>, fallback: T) => p.catch(() => fallback);
	const [items, categories, accounts, suggestions] = await Promise.all([
		v.recurring.list(),
		quiet(v.categories(), [] as Category[]),
		quiet(v.accounts.list(), [] as Account[]),
		quiet(v.billSuggestions.list(), [] as BillSuggestion[])
	]);
	// which one-offs are paid (their single occurrence), for their Paid / Late / Planned badge
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
	return { ...empty, items, categories, accounts, suggestions, oneOffPaid };
};

export const actions: Actions = { quickAdd: quickAddAction };
