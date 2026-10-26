import { fail, redirect } from '@sveltejs/kit';
import type { Actions } from './$types';
import type { AuthResult } from '#lib/types.ts';
import { applyAuth, isApiError, publicPost } from '#lib/server/api.ts';

export const actions: Actions = {
	default: async (event) => {
		const data = await event.request.formData();
		const displayName = String(data.get('displayName') ?? '').trim();
		const email = String(data.get('email') ?? '')
			.trim()
			.toLowerCase();
		const password = String(data.get('password') ?? '');
		const values = { displayName, email };

		if (displayName.length < 1 || displayName.length > 40) {
			return fail(400, { ...values, error: 'Pick a name between 1 and 40 characters.' });
		}
		if (!email.includes('@')) return fail(400, { ...values, error: 'Enter a valid email.' });
		if (password.length < 10) {
			return fail(400, { ...values, error: 'Use a password with at least 10 characters.' });
		}

		try {
			const auth = await publicPost<AuthResult>('/auth/register', { email, password, displayName });
			applyAuth(event, auth);
		} catch (e) {
			if (isApiError(e, 409))
				return fail(409, {
					...values,
					error: 'That email already has an account. Sign in instead?'
				});
			if (isApiError(e, 429))
				return fail(429, { ...values, error: 'Too many attempts. Please wait a minute.' });
			if (isApiError(e)) {
				return fail(e.status, {
					...values,
					error: e.field('password') ?? e.field('email') ?? e.field('displayName') ?? e.message
				});
			}
			return fail(503, { ...values, error: 'Could not reach the server. Try again shortly.' });
		}
		redirect(303, '/week');
	}
};
