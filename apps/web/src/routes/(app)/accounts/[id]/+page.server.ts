import { error, fail, redirect } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type { Account, BalanceSnapshot } from '#lib/types.ts';
import { api, isApiError } from '#lib/server/api.ts';
import { echo, parseAccountForm } from '#lib/server/forms.ts';

export const load: PageServerLoad = async (event) => {
	if (!event.locals.household) redirect(303, '/couple');
	const client = api(event);
	try {
		const [account, history] = await Promise.all([
			client.get<Account>(`/accounts/${event.params.id}`),
			client.get<BalanceSnapshot[]>(`/accounts/${event.params.id}/history?limit=30`)
		]);
		return { account, history, created: event.url.searchParams.has('created') };
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
