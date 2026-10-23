import { fail, redirect } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type { Account } from '#lib/types.ts';
import { api, isApiError } from '#lib/server/api.ts';
import { echo, parseAccountForm } from '#lib/server/forms.ts';

export const load: PageServerLoad = ({ locals }) => {
	if (!locals.household) redirect(303, '/couple');
	return {};
};

export const actions: Actions = {
	default: async (event) => {
		const data = await event.request.formData();
		const parsed = parseAccountForm(data);
		if (!parsed.ok) return fail(400, { error: parsed.error, values: echo(data) });
		let created: Account;
		try {
			created = await api(event).post<Account>('/accounts', parsed.value);
		} catch (e) {
			const message = isApiError(e) ? e.message : 'Could not save. Try again.';
			return fail(isApiError(e) ? e.status : 500, { error: message, values: echo(data) });
		}
		redirect(303, `/accounts/${created.id}?created=1`);
	}
};
