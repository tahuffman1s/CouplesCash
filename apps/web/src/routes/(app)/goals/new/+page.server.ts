import { fail, redirect } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type { Account } from '#lib/types.ts';
import { api, endpoints, isApiError } from '#lib/server/api.ts';
import { echo, parseGoalForm } from '#lib/server/forms.ts';

const LINKABLE = new Set(['savings', 'investment', 'cash']);

export const load: PageServerLoad = async (event) => {
	if (!event.locals.household) redirect(303, '/couple');
	const accounts = await api(event).get<Account[]>('/accounts');
	return { accounts: accounts.filter((a) => LINKABLE.has(a.type)) };
};

export const actions: Actions = {
	default: async (event) => {
		const data = await event.request.formData();
		const parsed = parseGoalForm(data);
		if (!parsed.ok) return fail(400, { error: parsed.error, values: echo(data) });
		let id: string;
		try {
			id = (await endpoints(event).goals.create(parsed.value)).id;
		} catch (e) {
			const message = isApiError(e) ? e.message : 'Could not save. Try again.';
			return fail(isApiError(e) ? e.status : 500, { error: message, values: echo(data) });
		}
		redirect(303, `/goals/${id}?created=1`);
	}
};
