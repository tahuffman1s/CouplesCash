import { fail } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import { endpoints, isApiError } from '#lib/server/api.ts';
import { echo, parseTodoForm } from '#lib/server/forms.ts';

export const load: PageServerLoad = async (event) => {
	const showDone = event.url.searchParams.get('done') === '1';
	if (!event.locals.household) return { todos: null, showDone, add: false };
	const todos = await endpoints(event).todos.list(showDone);
	return { todos, showDone, add: event.url.searchParams.has('add') };
};

export const actions: Actions = {
	add: async (event) => {
		const data = await event.request.formData();
		const parsed = parseTodoForm(data);
		if (!parsed.ok) return fail(400, { error: parsed.error, values: echo(data) });
		try {
			await endpoints(event).todos.create(parsed.value);
		} catch (e) {
			const message = isApiError(e) ? e.message : 'Could not save. Try again.';
			return fail(isApiError(e) ? e.status : 500, { error: message, values: echo(data) });
		}
		return { added: parsed.value.title };
	},
	delete: async (event) => {
		const data = await event.request.formData();
		const id = String(data.get('id') ?? '');
		try {
			await endpoints(event).todos.remove(id);
		} catch (e) {
			if (!isApiError(e, 404)) return fail(500, { error: 'Could not delete it.' });
		}
		return { deleted: true };
	}
};
