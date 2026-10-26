import { error, fail, redirect } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type { Account, Category } from '#lib/types.ts';
import { api, endpoints, isApiError } from '#lib/server/api.ts';
import { echo, parseExpenseForm } from '#lib/server/forms.ts';

export const load: PageServerLoad = async (event) => {
	if (!event.locals.household) redirect(303, '/couple');
	const client = api(event);
	try {
		const [expense, categories, accounts] = await Promise.all([
			endpoints(event).expenses.get(event.params.id),
			client.get<Category[]>('/categories'),
			client.get<Account[]>('/accounts')
		]);
		return { expense, categories, accounts };
	} catch (e) {
		if (isApiError(e, 404)) error(404, 'Expense not found');
		throw e;
	}
};

export const actions: Actions = {
	update: async (event) => {
		const data = await event.request.formData();
		const parsed = parseExpenseForm(data);
		if (!parsed.ok) return fail(400, { error: parsed.error, values: echo(data) });
		try {
			await endpoints(event).expenses.update(event.params.id, parsed.value);
		} catch (e) {
			const message = isApiError(e) ? e.message : 'Could not save. Try again.';
			return fail(isApiError(e) ? e.status : 500, { error: message, values: echo(data) });
		}
		return { saved: true };
	},
	delete: async (event) => {
		try {
			await endpoints(event).expenses.remove(event.params.id);
		} catch (e) {
			if (!isApiError(e, 404)) return fail(500, { error: 'Could not delete the expense.' });
		}
		redirect(303, '/transactions');
	}
};
