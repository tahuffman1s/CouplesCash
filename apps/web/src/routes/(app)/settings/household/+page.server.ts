import { fail, redirect } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type { Account } from '#lib/types.ts';
import { api, endpoints, isApiError } from '#lib/server/api.ts';
import { echo, parseHouseholdSettings } from '#lib/server/forms.ts';

export const load: PageServerLoad = async (event) => {
	if (!event.locals.household) redirect(303, '/couple');
	const [household, accounts] = await Promise.all([
		endpoints(event).household.get(),
		api(event).get<Account[]>('/accounts')
	]);
	if (!household) redirect(303, '/couple');
	return { household, accounts };
};

async function save(event: Parameters<Actions[string]>[0], section: string) {
	const data = await event.request.formData();
	const parsed = parseHouseholdSettings(data);
	if (!parsed.ok) return fail(400, { section, error: parsed.error, values: echo(data) });
	try {
		await endpoints(event).household.update(parsed.value);
	} catch (e) {
		const message = isApiError(e)
			? (e.field('currency') ??
				e.field('timezone') ??
				e.field('payAnchorDate') ??
				e.field('defaultAccountId') ??
				e.field('weeklySpendCents') ??
				e.message)
			: 'Could not save. Try again.';
		return fail(isApiError(e) ? e.status : 500, { section, error: message, values: echo(data) });
	}
	return { section, saved: true };
}

export const actions: Actions = {
	general: (event) => save(event, 'general'),
	payperiod: (event) => save(event, 'payperiod'),
	timezone: (event) => save(event, 'timezone'),
	weekly: (event) => save(event, 'weekly'),
	transfers: (event) => save(event, 'transfers')
};
