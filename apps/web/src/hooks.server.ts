import { json, redirect } from '@sveltejs/kit';
import type { Handle } from '@sveltejs/kit/hooks';
import { building } from '$app/env';
import { decodeJwt } from '#lib/server/jwt.ts';
import { ACCESS_COOKIE, REFRESH_COOKIE, isSecure, readTheme } from '#lib/server/cookies.ts';
import { refreshSession } from '#lib/server/api.ts';

const PUBLIC_PATHS = new Set(['/login', '/register', '/offline', '/manifest.webmanifest']);
const AUTH_PAGES = new Set(['/login', '/register']);
export const PROXY_HEADER = 'x-requested-with';
export const PROXY_HEADER_VALUE = 'CouplesCash';

function isPublic(pathname: string): boolean {
	if (PUBLIC_PATHS.has(pathname)) return true;
	// static assets, icons, service worker and Kit internals
	return (
		pathname.startsWith('/icons/') ||
		pathname.startsWith('/_app/') ||
		pathname === '/service-worker.js' ||
		pathname === '/favicon.ico' ||
		pathname === '/robots.txt'
	);
}

export const handle: Handle = async ({ event, resolve }) => {
	const { pathname } = event.url;

	// 1. Theme
	event.locals.theme = readTheme(event.cookies);

	// 2. Session: decode the access token; if it is missing/expired but we hold a refresh token,
	//    rotate it now so SSR renders authenticated on the first pass.
	let user = building ? null : decodeJwt(event.cookies.get(ACCESS_COOKIE));
	if (!building && !user && event.cookies.get(REFRESH_COOKIE)) {
		if (await refreshSession(event)) user = decodeJwt(event.cookies.get(ACCESS_COOKIE));
	}
	event.locals.user = user;
	event.locals.household = user?.household ?? null;

	// 3. Route protection (skipped while prerendering the public /offline page)
	const isApi = pathname === '/api' || pathname.startsWith('/api/');
	if (building) {
		// nothing to protect at build time
	} else if (isApi) {
		if (event.request.headers.get(PROXY_HEADER) !== PROXY_HEADER_VALUE) {
			return json(
				{ title: 'Forbidden', status: 403, detail: 'Missing X-Requested-With' },
				{ status: 403 }
			);
		}
		if (!user) return json({ title: 'Unauthorized', status: 401 }, { status: 401 });
	} else if (!user && !isPublic(pathname)) {
		const next = pathname + event.url.search;
		redirect(303, `/login${next !== '/' ? `?next=${encodeURIComponent(next)}` : ''}`);
	} else if (user && AUTH_PAGES.has(pathname)) {
		redirect(303, '/');
	}

	// 4. Render, stamping the theme on <html>
	const themeAttr =
		event.locals.theme === 'system'
			? ''
			: ` data-theme="${event.locals.theme === 'dark' ? 'couplescash-dark' : 'couplescash'}"`;

	const response = await resolve(event, {
		transformPageChunk: ({ html }) => html.replace('%theme%', themeAttr)
	});

	// 5. Security headers (CSP is emitted by SvelteKit from the `csp` config with nonces)
	const h = response.headers;
	h.set('X-Content-Type-Options', 'nosniff');
	h.set('Referrer-Policy', 'strict-origin-when-cross-origin');
	h.set('Permissions-Policy', 'camera=(self), microphone=(), geolocation=(), payment=()');
	h.set('X-Frame-Options', 'DENY');
	if (isSecure(event.url)) {
		h.set('Strict-Transport-Security', 'max-age=63072000; includeSubDomains');
	}
	return response;
};
