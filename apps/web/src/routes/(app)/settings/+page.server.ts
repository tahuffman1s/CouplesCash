import { fail, redirect, type RequestEvent } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type {
	Account,
	AuthResult,
	BankLink,
	Household,
	MatcherStatus,
	PushPrefs,
	User
} from '#lib/types.ts';
import {
	api,
	applyAuth,
	endpoints,
	isApiError,
	logout,
	publicPost,
	refreshSession
} from '#lib/server/api.ts';
import { setThemeCookie, type Theme } from '#lib/server/cookies.ts';
import { echo, parseHouseholdSettings } from '#lib/server/forms.ts';

/**
 * v6: one settings page (PLAN-V6 §4) with three sections: You (name, password, passkeys, theme,
 * notifications), Household (name, currency, week start, pay period, weekly target, partner,
 * auto-match level, transfers) and Banks (linked banks, CSV import, re-check matches).
 */
export const load: PageServerLoad = async (event) => {
	const v = endpoints(event);
	const quiet = <T>(p: Promise<T>, fallback: T) => p.catch(() => fallback);
	const hasHousehold = !!event.locals.household;
	const [me, vapidPublicKey, accounts, banks, matcher, csvCount] = await Promise.all([
		api(event).get<{ user: User; household: Household | null }>('/auth/me'),
		quiet(v.push.vapidPublicKey(), null as string | null),
		hasHousehold ? quiet(api(event).get<Account[]>('/accounts'), [] as Account[]) : [],
		hasHousehold ? quiet(v.banks.list(), null as BankLink[] | null) : null,
		hasHousehold ? quiet(v.matcher.status(), null as MatcherStatus | null) : null,
		hasHousehold
			? quiet(
					v.imports.listCsv().then((l) => l.length),
					0
				)
			: 0
	]);
	const prefs = vapidPublicKey ? await quiet(v.push.prefs(), null as PushPrefs | null) : null;
	return { me, vapidPublicKey, prefs, accounts, banks, matcher, csvCount };
};

async function saveHousehold(event: RequestEvent, section: string) {
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
				e.field('autoMatchLevel') ??
				e.message)
			: 'Could not save. Try again.';
		return fail(isApiError(e) ? e.status : 500, { section, error: message, values: echo(data) });
	}
	return { section, saved: true };
}

export const actions: Actions = {
	// --- You
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
	prefs: async (event) => {
		const data = await event.request.formData();
		const daysBefore = data
			.getAll('daysBefore')
			.map(Number)
			.filter((n) => Number.isInteger(n) && n >= 0 && n <= 14)
			.slice(0, 5);
		const hourLocal = Number(data.get('hourLocal') ?? 9);
		if (!Number.isInteger(hourLocal) || hourLocal < 0 || hourLocal > 23)
			return fail(400, { pushError: 'Pick an hour.' });
		try {
			const prefs = await endpoints(event).push.updatePrefs({
				remindersEnabled: data.get('remindersEnabled') === 'on',
				daysBefore: daysBefore.length ? daysBefore : [1],
				hourLocal,
				activityEnabled: data.get('activityEnabled') === 'on',
				weekSummaryEnabled: data.get('weekSummaryEnabled') === 'on'
			});
			return { prefs, saved: true };
		} catch (e) {
			return fail(isApiError(e) ? e.status : 500, {
				pushError: isApiError(e) ? e.message : 'Could not save your preferences.'
			});
		}
	},
	pushTest: async (event) => {
		try {
			await endpoints(event).push.test();
			return { tested: true };
		} catch (e) {
			return fail(isApiError(e) ? e.status : 500, {
				pushError: isApiError(e) ? e.message : 'Could not send a test notification.'
			});
		}
	},
	pushRemove: async (event) => {
		const data = await event.request.formData();
		const id = String(data.get('id') ?? '');
		const endpoint = String(data.get('endpoint') ?? '');
		try {
			await endpoints(event).push.unsubscribe(endpoint ? { endpoint, id } : { id });
			return { removed: true };
		} catch (e) {
			return fail(isApiError(e) ? e.status : 500, { pushError: 'Could not remove that device.' });
		}
	},
	signout: async (event) => {
		await logout(event);
		redirect(303, '/login');
	},
	// --- Household
	household: (event) => saveHousehold(event, 'household'),
	payperiod: (event) => saveHousehold(event, 'payperiod'),
	weekly: (event) => saveHousehold(event, 'weekly'),
	matching: (event) => saveHousehold(event, 'matching'),
	// --- Banks
	rematch: async (event) => {
		try {
			await endpoints(event).matcher.rematch();
			return { rematched: true };
		} catch (e) {
			if (isApiError(e, 429))
				return fail(429, { rematchError: 'Checked a moment ago. Try again in a minute.' });
			return fail(isApiError(e) ? e.status : 500, {
				rematchError: 'Could not re-check right now.'
			});
		}
	}
};
