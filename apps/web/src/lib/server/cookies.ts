import type { Cookies } from '@sveltejs/kit';
import type { AuthResult } from '#lib/types.ts';

export const ACCESS_COOKIE = 'cc_access';
export const REFRESH_COOKIE = 'cc_refresh';
export const THEME_COOKIE = 'theme';

export const ACCESS_MAX_AGE = 15 * 60;
export const REFRESH_MAX_AGE = 30 * 24 * 60 * 60;
export const THEME_MAX_AGE = 365 * 24 * 60 * 60;

/** Cookies are `Secure` whenever the app is served over https (dev and local preview are http). */
export function isSecure(url: URL): boolean {
	return url.protocol === 'https:';
}

export function setAuthCookies(cookies: Cookies, url: URL, auth: AuthResult): void {
	const secure = isSecure(url);
	cookies.set(ACCESS_COOKIE, auth.accessToken, {
		httpOnly: true,
		secure,
		sameSite: 'lax',
		path: '/',
		maxAge: Math.min(ACCESS_MAX_AGE, auth.accessTokenExpiresIn || ACCESS_MAX_AGE)
	});
	cookies.set(REFRESH_COOKIE, auth.refreshToken, {
		httpOnly: true,
		secure,
		sameSite: 'lax',
		path: '/',
		maxAge: REFRESH_MAX_AGE
	});
}

export function clearAuthCookies(cookies: Cookies, url: URL): void {
	const secure = isSecure(url);
	cookies.delete(ACCESS_COOKIE, { path: '/', secure });
	cookies.delete(REFRESH_COOKIE, { path: '/', secure });
}

export type Theme = 'system' | 'light' | 'dark';

export function readTheme(cookies: Cookies): Theme {
	const v = cookies.get(THEME_COOKIE);
	return v === 'light' || v === 'dark' ? v : 'system';
}

export function setThemeCookie(cookies: Cookies, url: URL, theme: Theme): void {
	cookies.set(THEME_COOKIE, theme, {
		httpOnly: false,
		secure: isSecure(url),
		sameSite: 'lax',
		path: '/',
		maxAge: THEME_MAX_AGE
	});
}
