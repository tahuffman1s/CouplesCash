import { fail, redirect } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type { AuthResult, CoupleCode, Household } from '#lib/types.ts';
import { api, applyAuth, isApiError, refreshSession } from '#lib/server/api.ts';
import { normaliseCoupleCode } from '#lib/utils/couple-code.ts';

export const load: PageServerLoad = async (event) => {
	const household = await api(event).get<Household | null>('/couple');
	return { household };
};

export const actions: Actions = {
	code: async (event) => {
		const data = await event.request.formData();
		const name = String(data.get('name') ?? '').trim();
		try {
			const code = await api(event).post<CoupleCode>('/couple/code', name ? { name } : {});
			// creating a household changes the `hh` claim – rotate the token so the rest of the app
			// sees the household immediately
			if (!event.locals.household) await refreshSession(event);
			return { code };
		} catch (e) {
			if (isApiError(e, 409))
				return fail(409, { error: 'Your household already has two members.' });
			return fail(isApiError(e) ? e.status : 500, {
				error: isApiError(e) ? e.message : 'Could not create a code right now.'
			});
		}
	},
	join: async (event) => {
		const data = await event.request.formData();
		const raw = String(data.get('code') ?? '');
		const code = normaliseCoupleCode(raw);
		if (!code)
			return fail(400, { joinError: 'That does not look like a Couple Code.', codeValue: raw });
		try {
			const auth = await api(event).post<AuthResult>('/couple/join', { code });
			applyAuth(event, auth);
		} catch (e) {
			if (isApiError(e, 404))
				return fail(404, {
					joinError: 'That code is invalid, expired or already used.',
					codeValue: raw
				});
			if (isApiError(e, 409))
				return fail(409, {
					joinError: 'You are already part of a household. Leave it first to join another.',
					codeValue: raw
				});
			if (isApiError(e, 429))
				return fail(429, {
					joinError: 'Too many attempts. Wait a minute and try again.',
					codeValue: raw
				});
			return fail(500, { joinError: 'Could not join right now.', codeValue: raw });
		}
		redirect(303, '/couple?joined=1');
	},
	leave: async (event) => {
		try {
			const auth = await api(event).post<AuthResult>('/couple/leave');
			applyAuth(event, auth);
		} catch {
			return fail(500, { error: 'Could not leave the household.' });
		}
		redirect(303, '/couple');
	},
	rename: async (event) => {
		const data = await event.request.formData();
		const name = String(data.get('name') ?? '').trim();
		if (!name) return fail(400, { error: 'Give your household a name.' });
		try {
			await api(event).patch<Household>('/couple', { name });
		} catch {
			return fail(500, { error: 'Could not rename the household.' });
		}
		return { renamed: true };
	}
};
