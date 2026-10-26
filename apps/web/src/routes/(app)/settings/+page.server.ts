import { fail, redirect, type RequestEvent } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type {
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
import {
	funRows,
	paycheckGuess,
	roleDefaults,
	roleRows,
	saveFun,
	savePayday,
	saveRoles,
	setupData
} from '#lib/server/setup.ts';

/**
 * v7: one settings page (PLAN-V7 "Navigation"): You (name, password, passkeys, theme, payday push
 * + bill reminders), Household (partner, payday, fun money, auto-match level), Accounts (role +
 * owner per account) and Banks (linked banks with feed diagnostics, re-check, import a statement).
 */
export const load: PageServerLoad = async (event) => {
	const v = endpoints(event);
	const quiet = <T>(p: Promise<T>, fallback: T) => p.catch(() => fallback);
	const hasHousehold = !!event.locals.household;
	const [me, vapidPublicKey, setup, matcher, csvCount] = await Promise.all([
		api(event).get<{ user: User; household: Household | null }>('/auth/me'),
		quiet(v.push.vapidPublicKey(), null as string | null),
		hasHousehold ? setupData(event) : null,
		hasHousehold ? quiet(v.matcher.status(), null as MatcherStatus | null) : null,
		hasHousehold
			? quiet(
					v.imports.listCsv().then((l) => l.length),
					0
				)
			: 0
	]);
	const prefs = vapidPublicKey ? await quiet(v.push.prefs(), null as PushPrefs | null) : null;
	const accounts = setup?.accounts ?? [];
	const banks = setup?.banks ?? null;
	const rows = roleRows(accounts, banks);
	return {
		me,
		vapidPublicKey,
		prefs,
		accounts,
		banks: banks as BankLink[] | null,
		matcher,
		csvCount,
		plan: setup?.plan ?? null,
		categories: setup?.categories ?? [],
		rows,
		defaults: roleDefaults(rows, me.household),
		funRows: funRows(accounts, setup?.items ?? []),
		paycheck: setup ? await paycheckGuess(event, setup.items, setup.plan) : null
	};
};

async function saveHousehold(event: RequestEvent, section: string) {
	const data = await event.request.formData();
	const parsed = parseHouseholdSettings(data);
	if (!parsed.ok) return fail(400, { section, error: parsed.error, values: echo(data) });
	try {
		await endpoints(event).household.update(parsed.value);
	} catch (e) {
		const message = isApiError(e)
			? (e.field('timezone') ?? e.field('paydayWeekday') ?? e.field('autoMatchLevel') ?? e.message)
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
			// v7: payday push + bill reminders only (partner activity and week summaries are gone;
			// the payday push rides on `weekSummaryEnabled` for API builds without `paydayEnabled`)
			const payday = data.get('paydayEnabled') === 'on';
			const prefs = await endpoints(event).push.updatePrefs({
				remindersEnabled: data.get('remindersEnabled') === 'on',
				daysBefore: daysBefore.length ? daysBefore : [1],
				hourLocal,
				activityEnabled: false,
				weekSummaryEnabled: payday,
				paydayEnabled: payday
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
	matching: (event) => saveHousehold(event, 'matching'),
	payday: async (event) => {
		const r = await savePayday(event, await event.request.formData());
		return 'ok' in r ? { section: 'payday', saved: true } : r;
	},
	fun: async (event) => {
		const r = await saveFun(event, await event.request.formData());
		return 'ok' in r ? { section: 'fun', saved: true } : r;
	},
	reset: async (event) => {
		const confirm = String((await event.request.formData()).get('confirm') ?? '');
		if (confirm.trim().toUpperCase() !== 'RESET')
			return fail(400, { resetError: 'Type RESET to confirm.' });
		try {
			await endpoints(event).household.reset(confirm);
		} catch (e) {
			return fail(isApiError(e) ? e.status : 500, {
				resetError: (isApiError(e) && e.field('confirm')) || 'Could not start over. Try again.'
			});
		}
		redirect(303, '/week');
	},
	// --- Accounts
	roles: async (event) => {
		const r = await saveRoles(event, await event.request.formData());
		return 'ok' in r ? { section: 'roles', saved: true } : r;
	},
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
