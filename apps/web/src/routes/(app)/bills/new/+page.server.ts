import { fail, redirect } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type { Account, Category, RecurringItem } from '#lib/types.ts';
import { api, isApiError } from '#lib/server/api.ts';
import { echo, parseRecurringForm } from '#lib/server/forms.ts';

export const load: PageServerLoad = async (event) => {
	if (!event.locals.household) redirect(303, '/couple');
	const client = api(event);
	const [categories, accounts] = await Promise.all([
		client.get<Category[]>('/categories'),
		client.get<Account[]>('/accounts')
	]);
	return { categories, accounts, kind: event.url.searchParams.get('kind') };
};

export const actions: Actions = {
	default: async (event) => {
		const data = await event.request.formData();
		const parsed = parseRecurringForm(data);
		if (!parsed.ok) return fail(400, { error: parsed.error, values: echo(data) });
		let created: RecurringItem;
		try {
			created = await api(event).post<RecurringItem>('/recurring', parsed.value);
		} catch (e) {
			const message = isApiError(e) ? e.message : 'Could not save. Try again.';
			return fail(isApiError(e) ? e.status : 500, { error: message, values: echo(data) });
		}
		redirect(303, `/bills/${created.id}?created=1`);
	}
};
