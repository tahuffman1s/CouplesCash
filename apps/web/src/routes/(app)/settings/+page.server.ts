import { fail, redirect } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type { AuthResult, Household, User } from '#lib/types.ts';
import { api, applyAuth, isApiError, logout, publicPost, refreshSession } from '#lib/server/api.ts';
import { setThemeCookie, type Theme } from '#lib/server/cookies.ts';

export const load: PageServerLoad = async (event) => {
	const me = await api(event).get<{ user: User; household: Household | null }>('/auth/me');
	return { me };
};

export const actions: Actions = {
	theme: async (event) => {
		const data = await event.request.formData();
		const t = String(data.get('theme') ?? 'system');
		const theme: Theme = t === 'light' || t === 'dark' ? t : 'system';
		setThemeCookie(event.cookies, event.url, theme);
		return { theme };
	},
	profile: async (event) => {
		const data = await event.request.formData();
		const displayName = String(data.get('displayName') ?? '').trim();
		if (displayName.length < 1 || displayName.length > 40) {
			return fail(400, { profileError: 'Pick a name between 1 and 40 characters.' });
		}
		try {
			await api(event).patch<User>('/auth/me', { displayName });
			// the name lives in the JWT too – rotate so the UI shows it everywhere
			await refreshSession(event);
		} catch (e) {
			return fail(isApiError(e) ? e.status : 500, {
				profileError: isApiError(e) ? e.message : 'Could not save your name.'
			});
		}
		return { profileSaved: true };
	},
	password: async (event) => {
		const data = await event.request.formData();
		const currentPassword = String(data.get('currentPassword') ?? '');
		const newPassword = String(data.get('newPassword') ?? '');
		const confirm = String(data.get('confirmPassword') ?? '');
		if (newPassword.length < 10) return fail(400, { passwordError: 'Use at least 10 characters.' });
		if (newPassword !== confirm) return fail(400, { passwordError: 'The passwords do not match.' });
		try {
			await api(event).post('/auth/password', { currentPassword, newPassword });
			// all refresh tokens are revoked by the API – sign back in with the new password so this
			// device stays logged in
			const auth = await publicPost<AuthResult>('/auth/login', {
				email: event.locals.user?.email,
				password: newPassword
			});
			applyAuth(event, auth);
		} catch (e) {
			if (isApiError(e, 401) || isApiError(e, 400)) {
				return fail(400, { passwordError: 'Your current password is incorrect.' });
			}
			return fail(500, { passwordError: 'Could not change the password.' });
		}
		return { passwordSaved: true };
	},
	signout: async (event) => {
		await logout(event);
		redirect(303, '/login');
	}
};
