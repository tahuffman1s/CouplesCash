import { error, fail, redirect } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type { Account } from '#lib/types.ts';
import { api, endpoints, isApiError } from '#lib/server/api.ts';
import { echo, parseGoalForm, str } from '#lib/server/forms.ts';
import { parseMoney } from '#lib/utils/money.ts';

const LINKABLE = new Set(['savings', 'investment', 'cash']);

export const load: PageServerLoad = async (event) => {
	if (!event.locals.household) redirect(303, '/couple');
	try {
		const [goal, accounts] = await Promise.all([
			endpoints(event).goals.get(event.params.id),
			api(event).get<Account[]>('/accounts')
		]);
		return {
			goal,
			accounts: accounts.filter((a) => LINKABLE.has(a.type)),
			created: event.url.searchParams.has('created')
		};
	} catch (e) {
		if (isApiError(e, 404)) error(404, 'Goal not found');
		throw e;
	}
};

export const actions: Actions = {
	update: async (event) => {
		const data = await event.request.formData();
		const parsed = parseGoalForm(data);
		if (!parsed.ok) return fail(400, { error: parsed.error, values: echo(data) });
		try {
			await endpoints(event).goals.update(event.params.id, parsed.value);
		} catch (e) {
			const message = isApiError(e) ? e.message : 'Could not save. Try again.';
			return fail(isApiError(e) ? e.status : 500, { error: message, values: echo(data) });
		}
		return { saved: true };
	},
	contribute: async (event) => {
		const data = await event.request.formData();
		const raw = str(data, 'amount');
		const sign = str(data, 'direction') === 'withdraw' ? -1 : 1;
		const cents = parseMoney(raw);
		if (cents === null || cents === 0)
			return fail(400, { contributeError: 'Enter an amount like 50.' });
		try {
			await endpoints(event).goals.contribute(event.params.id, sign * cents);
		} catch (e) {
			return fail(isApiError(e) ? e.status : 500, {
				contributeError: isApiError(e) ? e.message : 'Could not record the contribution.'
			});
		}
		return { contributed: sign * cents };
	},
	delete: async (event) => {
		try {
			await endpoints(event).goals.remove(event.params.id);
		} catch (e) {
			if (!isApiError(e, 404)) return fail(500, { error: 'Could not remove the goal.' });
		}
		redirect(303, '/goals');
	}
};
