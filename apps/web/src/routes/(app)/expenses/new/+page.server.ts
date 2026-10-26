import { fail, redirect } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type { Account, Category } from '#lib/types.ts';
import { api, endpoints, isApiError } from '#lib/server/api.ts';
import { echo, parseExpenseForm } from '#lib/server/forms.ts';

export const load: PageServerLoad = async (event) => {
	if (!event.locals.household) redirect(303, '/couple');
	const client = api(event);
	const [categories, accounts] = await Promise.all([
		client.get<Category[]>('/categories'),
		client.get<Account[]>('/accounts')
	]);
	// prefill from the quick-add sheet ("Already spent? Add an expense")
	const q = event.url.searchParams;
	const name = q.get('name')?.slice(0, 80);
	const amount = q.get('amount')?.slice(0, 20);
	const prefill =
		name || amount ? { name: name ?? '', amount: amount ?? '', deduct: 'on' } : undefined;
	return { categories, accounts, prefill };
};

export const actions: Actions = {
	default: async (event) => {
		const data = await event.request.formData();
		const parsed = parseExpenseForm(data);
		if (!parsed.ok) return fail(400, { error: parsed.error, values: echo(data) });
		try {
			await endpoints(event).expenses.create(parsed.value);
		} catch (e) {
			const message = isApiError(e) ? e.message : 'Could not save. Try again.';
			return fail(isApiError(e) ? e.status : 500, { error: message, values: echo(data) });
		}
		redirect(303, '/transactions?added=1');
	}
};
