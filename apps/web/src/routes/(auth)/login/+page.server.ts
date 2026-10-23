import { fail, redirect } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type { AuthResult } from '#lib/types.ts';
import { applyAuth, isApiError, passkeyConfig, publicPost } from '#lib/server/api.ts';

function safeNext(raw: string | null): string {
	if (!raw || !raw.startsWith('/') || raw.startsWith('//')) return '/';
	return raw;
}

export const load: PageServerLoad = async (event) => {
	const config = await passkeyConfig();
	return { passkeysEnabled: config.enabled, next: safeNext(event.url.searchParams.get('next')) };
};

export const actions: Actions = {
	default: async (event) => {
		const data = await event.request.formData();
		const email = String(data.get('email') ?? '')
			.trim()
			.toLowerCase();
		const password = String(data.get('password') ?? '');
		if (!email || !password) {
			return fail(400, { email, error: 'Enter your email and password.' });
		}
		try {
			const auth = await publicPost<AuthResult>('/auth/login', { email, password });
			applyAuth(event, auth);
		} catch (e) {
			if (isApiError(e, 401)) return fail(401, { email, error: 'Email or password is incorrect.' });
			if (isApiError(e, 429))
				return fail(429, { email, error: 'Too many attempts. Please wait a minute.' });
			if (isApiError(e)) return fail(e.status, { email, error: e.message });
			return fail(503, { email, error: 'Could not reach the server. Try again shortly.' });
		}
		redirect(303, safeNext(event.url.searchParams.get('next')));
	}
};
