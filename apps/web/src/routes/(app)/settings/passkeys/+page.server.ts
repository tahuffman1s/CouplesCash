import { fail } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type { Passkey } from '#lib/types.ts';
import { endpoints, isApiError, passkeyConfig } from '#lib/server/api.ts';

export const load: PageServerLoad = async (event) => {
	const config = await passkeyConfig();
	const passkeys = config.enabled
		? await endpoints(event)
				.passkeys.list()
				.catch(() => [] as Passkey[])
		: [];
	return { config, passkeys };
};

export const actions: Actions = {
	delete: async (event) => {
		const data = await event.request.formData();
		const id = String(data.get('id') ?? '');
		try {
			await endpoints(event).passkeys.remove(id);
			return { deleted: true };
		} catch (e) {
			if (isApiError(e, 404)) return { deleted: true };
			return fail(500, { error: 'Could not remove the passkey.' });
		}
	}
};
