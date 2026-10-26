import { error, fail, redirect } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type {
	Account,
	AccountTransactionsPage,
	BalanceSnapshot,
	Category,
	TxFilter
} from '#lib/types.ts';
import { TX_FILTERS } from '#lib/types.ts';
import { api, endpoints, isApiError } from '#lib/server/api.ts';
import { today } from '#lib/utils/dates.ts';
import { echo, parseAccountForm } from '#lib/server/forms.ts';

export const load: PageServerLoad = async (event) => {
	if (!event.locals.household) redirect(303, '/couple');
	const client = api(event);
	try {
		const [account, history] = await Promise.all([
			client.get<Account>(`/accounts/${event.params.id}`),
			client.get<BalanceSnapshot[]>(`/accounts/${event.params.id}/history?limit=30`)
		]);
		// v5: Transactions tab (first page server-side; filters, search and pages go via /api)
		const rawFilter = event.url.searchParams.get('filter');
		const filter: TxFilter = TX_FILTERS.some((f) => f.value === rawFilter)
			? (rawFilter as TxFilter)
			: 'all';
		const tab =
			event.url.searchParams.get('tab') === 'transactions' || rawFilter
				? 'transactions'
				: 'overview';
		const m = event.url.searchParams.get('month');
		const month = m && /^\d{4}-(0[1-9]|1[0-2])$/.test(m) ? m : today().slice(0, 7);
		let transactions: AccountTransactionsPage | null = null;
		let categories: Category[] = [];
		if (tab === 'transactions') {
			[transactions, categories] = await Promise.all([
				endpoints(event)
					.accounts.transactions(event.params.id, { month, filter, limit: 50 })
					.catch(() => null),
				client.get<Category[]>('/categories').catch(() => [] as Category[])
			]);
		}
		return {
			account,
			history,
			tab,
			month,
			filter,
			transactions,
			categories,
			created: event.url.searchParams.has('created')
		};
	} catch (e) {
		if (isApiError(e, 404)) error(404, 'Account not found');
		throw e;
	}
};

export const actions: Actions = {
	update: async (event) => {
		const data = await event.request.formData();
		const parsed = parseAccountForm(data);
		if (!parsed.ok) return fail(400, { error: parsed.error, values: echo(data) });
		try {
			await api(event).put<Account>(`/accounts/${event.params.id}`, parsed.value);
		} catch (e) {
			const message = isApiError(e) ? e.message : 'Could not save. Try again.';
			return fail(isApiError(e) ? e.status : 500, { error: message, values: echo(data) });
		}
		return { saved: true };
	},
	delete: async (event) => {
		try {
			await api(event).del(`/accounts/${event.params.id}`);
		} catch (e) {
			if (!isApiError(e, 404)) return fail(500, { error: 'Could not remove the account.' });
		}
		redirect(303, '/accounts');
	}
};
