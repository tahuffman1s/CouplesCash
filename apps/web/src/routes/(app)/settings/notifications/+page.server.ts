import { fail } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type { PushPrefs } from '#lib/types.ts';
import { endpoints, isApiError } from '#lib/server/api.ts';

export const load: PageServerLoad = async (event) => {
	const v2 = endpoints(event);
	const vapidPublicKey = await v2.push.vapidPublicKey();
	const prefs = vapidPublicKey ? await v2.push.prefs().catch(() => null as PushPrefs | null) : null;
	return { vapidPublicKey, prefs };
};

export const actions: Actions = {
	prefs: async (event) => {
		const data = await event.request.formData();
		const daysBefore = data
			.getAll('daysBefore')
			.map(Number)
			.filter((n) => Number.isInteger(n) && n >= 0 && n <= 14)
			.slice(0, 5);
		const hourLocal = Number(data.get('hourLocal') ?? 9);
		if (!Number.isInteger(hourLocal) || hourLocal < 0 || hourLocal > 23)
			return fail(400, { error: 'Pick an hour.' });
		try {
			const prefs = await endpoints(event).push.updatePrefs({
				remindersEnabled: data.get('remindersEnabled') === 'on',
				daysBefore: daysBefore.length ? daysBefore : [1],
				hourLocal,
				activityEnabled: data.get('activityEnabled') === 'on'
			});
			return { prefs, saved: true };
		} catch (e) {
			return fail(isApiError(e) ? e.status : 500, {
				error: isApiError(e) ? e.message : 'Could not save your preferences.'
			});
		}
	},
	test: async (event) => {
		try {
			await endpoints(event).push.test();
			return { tested: true };
		} catch (e) {
			return fail(isApiError(e) ? e.status : 500, {
				error: isApiError(e) ? e.message : 'Could not send a test notification.'
			});
		}
	},
	remove: async (event) => {
		const data = await event.request.formData();
		const id = String(data.get('id') ?? '');
		const endpoint = String(data.get('endpoint') ?? '');
		try {
			await endpoints(event).push.unsubscribe(endpoint ? { endpoint, id } : { id });
			return { removed: true };
		} catch (e) {
			return fail(isApiError(e) ? e.status : 500, { error: 'Could not remove that device.' });
		}
	}
};
