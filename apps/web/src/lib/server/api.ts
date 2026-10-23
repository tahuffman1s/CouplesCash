import { API_URL } from '$app/env/private';
import { redirect, type RequestEvent } from '@sveltejs/kit';
import type { AuthResult, Problem } from '#lib/types.ts';
import { ACCESS_COOKIE, REFRESH_COOKIE, clearAuthCookies, setAuthCookies } from './cookies';

export const API_BASE = `${API_URL}/v1`;

/** Thrown for any non-2xx API response. Carries the RFC 9457 problem. */
export class ApiError extends Error {
	readonly status: number;
	readonly problem: Problem;

	constructor(status: number, problem: Problem) {
		super(problem.detail || problem.title || `API error ${status}`);
		this.name = 'ApiError';
		this.status = status;
		this.problem = problem;
	}

	/** First validation message for a field, if any. */
	field(name: string): string | undefined {
		const errors = this.problem.errors;
		if (!errors) return undefined;
		const key = Object.keys(errors).find((k) => k.toLowerCase() === name.toLowerCase());
		return key ? errors[key]?.[0] : undefined;
	}
}

export function isApiError(e: unknown, status?: number): e is ApiError {
	return e instanceof ApiError && (status === undefined || e.status === status);
}

async function readProblem(res: Response): Promise<Problem> {
	const fallback: Problem = { title: res.statusText || 'Request failed', status: res.status };
	const type = res.headers.get('content-type') ?? '';
	if (!type.includes('json')) return fallback;
	try {
		const body = (await res.json()) as Partial<Problem>;
		return { ...fallback, ...body, status: body.status ?? res.status };
	} catch {
		return fallback;
	}
}

type UnauthorizedMode = 'redirect' | 'return';

function headersFor(token: string | undefined, init?: RequestInit): Headers {
	const headers = new Headers(init?.headers);
	headers.set('accept', 'application/json, application/problem+json');
	if (token) headers.set('authorization', `Bearer ${token}`);
	return headers;
}

/**
 * Rotates the refresh token and sets fresh cookies. Deduplicated per request so that parallel
 * `load` calls never present the same refresh token twice (the API would revoke the family).
 */
export function refreshSession(event: RequestEvent): Promise<boolean> {
	if (event.locals.refreshing) return event.locals.refreshing;
	const refreshToken = event.cookies.get(REFRESH_COOKIE);
	if (!refreshToken) return Promise.resolve(false);

	event.locals.refreshing = (async () => {
		try {
			const res = await fetch(`${API_BASE}/auth/refresh`, {
				method: 'POST',
				headers: { 'content-type': 'application/json', accept: 'application/json' },
				body: JSON.stringify({ refreshToken })
			});
			if (!res.ok) {
				clearAuthCookies(event.cookies, event.url);
				return false;
			}
			const auth = (await res.json()) as AuthResult;
			setAuthCookies(event.cookies, event.url, auth);
			return true;
		} catch {
			return false;
		}
	})();
	return event.locals.refreshing;
}

/**
 * Authenticated fetch against the API. Adds the bearer token from `cc_access`; on 401 it refreshes
 * once and retries. If the refresh fails the cookies are cleared and the caller is redirected to
 * `/login` (or the 401 response is returned when `unauthorized === 'return'`).
 */
export async function authFetch(
	event: RequestEvent,
	path: string,
	init: RequestInit = {},
	unauthorized: UnauthorizedMode = 'redirect'
): Promise<Response> {
	const url = path.startsWith('http') ? path : `${API_BASE}${path}`;
	const attempt = (token: string | undefined) =>
		fetch(url, { ...init, headers: headersFor(token, init) });

	let token = event.cookies.get(ACCESS_COOKIE);
	if (!token && event.cookies.get(REFRESH_COOKIE)) {
		if (await refreshSession(event)) token = event.cookies.get(ACCESS_COOKIE);
	}

	let res = await attempt(token);
	if (res.status === 401 && event.cookies.get(REFRESH_COOKIE)) {
		// bodies must be re-sent on retry, so callers pass strings/buffers, never streams
		if (await refreshSession(event)) {
			res = await attempt(event.cookies.get(ACCESS_COOKIE));
		}
	}

	if (res.status === 401) {
		clearAuthCookies(event.cookies, event.url);
		if (unauthorized === 'redirect') {
			const next = event.url.pathname + event.url.search;
			redirect(303, `/login${next && next !== '/' ? `?next=${encodeURIComponent(next)}` : ''}`);
		}
	}
	return res;
}

async function parse<T>(res: Response): Promise<T> {
	if (!res.ok) throw new ApiError(res.status, await readProblem(res));
	if (res.status === 204 || res.headers.get('content-length') === '0') return undefined as T;
	return (await res.json()) as T;
}

function jsonInit(method: string, body?: unknown): RequestInit {
	return {
		method,
		headers: body === undefined ? {} : { 'content-type': 'application/json' },
		body: body === undefined ? undefined : JSON.stringify(body)
	};
}

/** Typed, cookie-aware API client bound to the current request. */
export function api(event: RequestEvent) {
	return {
		get: <T>(path: string) => authFetch(event, path, { method: 'GET' }).then(parse<T>),
		post: <T>(path: string, body?: unknown) =>
			authFetch(event, path, jsonInit('POST', body)).then(parse<T>),
		put: <T>(path: string, body?: unknown) =>
			authFetch(event, path, jsonInit('PUT', body)).then(parse<T>),
		patch: <T>(path: string, body?: unknown) =>
			authFetch(event, path, jsonInit('PATCH', body)).then(parse<T>),
		del: <T = void>(path: string) => authFetch(event, path, { method: 'DELETE' }).then(parse<T>)
	};
}

export type Api = ReturnType<typeof api>;

/** Unauthenticated POST (register / login / refresh). */
export async function publicPost<T>(path: string, body: unknown): Promise<T> {
	const res = await fetch(`${API_BASE}${path}`, {
		...jsonInit('POST', body),
		headers: { 'content-type': 'application/json', accept: 'application/json' }
	});
	return parse<T>(res);
}

/** Applies an AuthResult (login, register, join, leave) to the session cookies. */
export function applyAuth(event: RequestEvent, auth: AuthResult): void {
	setAuthCookies(event.cookies, event.url, auth);
}

/** Best-effort logout: revoke the refresh token at the API, then drop both cookies. */
export async function logout(event: RequestEvent): Promise<void> {
	const refreshToken = event.cookies.get(REFRESH_COOKIE);
	if (refreshToken) {
		try {
			await authFetch(event, '/auth/logout', jsonInit('POST', { refreshToken }), 'return');
		} catch {
			// ignore – cookies are cleared regardless
		}
	}
	clearAuthCookies(event.cookies, event.url);
}
