import { fail, redirect } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type { AuthResult, Household } from '#lib/types.ts';
import { api, applyAuth, isApiError } from '#lib/server/api.ts';
import { normaliseCoupleCode } from '#lib/utils/couple-code.ts';

export const load: PageServerLoad = async (event) => {
	const raw = event.url.searchParams.get('c') ?? '';
	const code = normaliseCoupleCode(raw);
	const household = await api(event).get<Household | null>('/couple');
	return { code, raw, household };
};

export const actions: Actions = {
	default: async (event) => {
		const data = await event.request.formData();
		const raw = String(data.get('code') ?? '');
		const code = normaliseCoupleCode(raw);
		if (!code) return fail(400, { error: 'That does not look like a Couple Code.' });
		try {
			const auth = await api(event).post<AuthResult>('/couple/join', { code });
			applyAuth(event, auth);
		} catch (e) {
			if (isApiError(e, 404))
				return fail(404, { error: 'That code is invalid, expired or already used.' });
			if (isApiError(e, 409)) return fail(409, { error: 'You are already part of a household.' });
			return fail(500, { error: 'Could not join right now.' });
		}
		redirect(303, '/couple?joined=1');
	}
};
