import { createHash } from 'node:crypto';
import { API_URL } from '$app/env/private';
import { redirect, type RequestEvent } from '@sveltejs/kit';
import type {
	Account,
	AccountRole,
	AccountTransaction,
	AccountTransactionsPage,
	AddTransactionBody,
	AccountType,
	AutoMatchLevel,
	AuthResult,
	BankLink,
	BillPayment,
	BillSuggestion,
	BulkResult,
	Category,
	CsvImportRecord,
	CsvImportResult,
	CsvImportRow,
	ExternalAccount,
	Frequency,
	FromTransactionResult,
	Household,
	InboxCandidates,
	InboxItem,
	InboxPage,
	InboxStatus,
	InboxSummary,
	Kind,
	MatcherStatus,
	Occurrence,
	Passkey,
	PasskeyConfig,
	Plan,
	Problem,
	PushPrefs,
	RecurringItem,
	Sweep,
	SyncResult,
	TransactionFeed
} from '#lib/types.ts';
import {
	accountTxQuery,
	transactionFeedQuery,
	type AccountTxQuery,
	type TransactionFeedQuery
} from '#lib/utils/transactions.ts';
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

type Rotation = { auth: AuthResult } | { rejected: true } | { failed: true };

/**
 * Rotations in flight or just done, by refresh token (hashed). A browser often sends several
 * requests at once when the access token has run out — a hover preload and a click, a form post
 * and its reload, two tabs — each with the same refresh token. Only the first may rotate it: the
 * API treats a second use as a stolen token and revokes the whole session. The others share the
 * first one's result, kept for {@link ROTATION_REUSE_MS} for requests that started just after.
 */
const rotations = new Map<string, { at: number; result: Promise<Rotation> }>();
const ROTATION_REUSE_MS = 30_000;

function rotate(refreshToken: string): Promise<Rotation> {
	const now = Date.now();
	for (const [key, r] of rotations) if (now - r.at > ROTATION_REUSE_MS) rotations.delete(key);
	const key = createHash('sha256').update(refreshToken).digest('base64url');
	const hit = rotations.get(key);
	if (hit) return hit.result;

	const result = (async (): Promise<Rotation> => {
		try {
			const res = await fetch(`${API_BASE}/auth/refresh`, {
				method: 'POST',
				headers: { 'content-type': 'application/json', accept: 'application/json' },
				body: JSON.stringify({ refreshToken })
			});
			if (!res.ok) return { rejected: true };
			return { auth: (await res.json()) as AuthResult };
		} catch {
			// the API was unreachable: nothing was rotated, so the next request may try again
			rotations.delete(key);
			return { failed: true };
		}
	})();
	rotations.set(key, { at: now, result });
	return result;
}

/**
 * Rotates the refresh token and sets fresh cookies. Deduplicated per request (parallel `load`
 * calls) and across requests that carry the same refresh token (see {@link rotate}), so the API
 * never sees one refresh token twice.
 */
export function refreshSession(event: RequestEvent): Promise<boolean> {
	if (event.locals.refreshing) return event.locals.refreshing;
	const refreshToken = event.cookies.get(REFRESH_COOKIE);
	if (!refreshToken) return Promise.resolve(false);

	event.locals.refreshing = rotate(refreshToken).then((r) => {
		if ('auth' in r) {
			setAuthCookies(event.cookies, event.url, r.auth);
			return true;
		}
		if ('rejected' in r) clearAuthCookies(event.cookies, event.url);
		return false;
	});
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

export type HouseholdPatch = Partial<{
	name: string;
	timezone: string;
	defaultAccountId: string | null;
	autoIgnoreTransfers: boolean;
	autoMatchLevel: AutoMatchLevel;
	/** v7: 0 = Sunday … 6 = Saturday */
	paydayWeekday: number;
}>;
export type RecurringBody = {
	name: string;
	kind: Kind;
	amountCents: number;
	categoryId: string | null;
	accountId: string | null;
	frequency: Frequency;
	anchorDate: string;
	endDate: string | null;
	autoPay: boolean;
	notes: string | null;
	url: string | null;
	/** v6: "Bank shows it as" (replaces the v4 `payeeHint`, which is no longer written). */
	bankNames: string[];
	amountVaries: boolean;
	/** v7: `kind: 'transfer'` requires both (different accounts in the household). */
	fromAccountId?: string | null;
	toAccountId?: string | null;
};
export type PushPrefsBody = Omit<PushPrefs, 'subscriptions'>;
export type ExternalAccountMapping =
	| { accountId: string | null }
	| { create: { name: string; type: AccountType; institution?: string } };
export type InboxConfirmBody =
	| { as: 'expense'; categoryId?: string | null; name?: string }
	| { as: 'occurrence'; itemId: string; dueDate: string };

/**
 * Typed v2 endpoints (docs/API-CONTRACT.md § v2 additions) on top of the generic client.
 * Everything here is household-scoped by the caller's token; ids never come from the browser
 * unvalidated – they are forwarded as route segments the API 404s on when foreign.
 */
export function endpoints(event: RequestEvent) {
	const c = api(event);
	return {
		categories: () => c.get<Category[]>('/categories'),
		household: {
			get: () => c.get<Household | null>('/couple'),
			update: (patch: HouseholdPatch) => c.patch<Household>('/couple', patch),
			// v7 "Start over": every account, bill, transaction and bank link, for both of you
			reset: (confirm: string) => c.post<Household>('/couple/reset', { confirm })
		},
		recurring: {
			create: (body: RecurringBody) => c.post<RecurringItem>('/recurring', body),
			update: (id: string, body: RecurringBody) => c.put<RecurringItem>(`/recurring/${id}`, body),
			remove: (id: string) => c.del(`/recurring/${id}`),
			// v5
			fromTransaction: (body: {
				transactionId: string;
				name?: string;
				kind?: Kind;
				frequency?: Frequency;
			}) => c.post<FromTransactionResult>('/recurring/from-transaction', body),
			payments: (id: string, limit = 12) =>
				c.get<BillPayment[]>(`/recurring/${id}/payments?limit=${limit}`),
			candidates: (id: string, date: string, days = 10) =>
				c.get<InboxItem[]>(`/recurring/${id}/occurrences/${date}/candidates?days=${days}`),
			link: (id: string, date: string, transactionId: string) =>
				c.post<Occurrence>(`/recurring/${id}/occurrences/${date}/link`, { transactionId }),
			list: () => c.get<RecurringItem[]>('/recurring'),
			// v7: all-or-nothing, ≤ 100 (per-index errors `items[3].amountCents`)
			bulk: (items: RecurringBody[]) =>
				c.post<{ items: RecurringItem[] }>('/recurring/bulk', { items }),
			markPaid: (id: string, date: string, amountCents?: number) =>
				c.put<Occurrence>(
					`/recurring/${id}/occurrences/${date}/paid`,
					amountCents === undefined ? {} : { amountCents }
				),
			markUnpaid: (id: string, date: string) =>
				c.del<Occurrence>(`/recurring/${id}/occurrences/${date}/paid`),
			// v6
			forgetLearned: (id: string, index: number) =>
				c.del<RecurringItem>(`/recurring/${id}/bank-names/learned/${index}`)
		},
		// ---- v6 (docs/API-CONTRACT.md § v6 additions)
		transactions: {
			feed: (opts: TransactionFeedQuery = {}) =>
				c.get<TransactionFeed>(`/transactions${transactionFeedQuery(opts)}`),
			// added by hand: pending until the bank's copy replaces it; 409 `likely_duplicate` unless force
			add: (body: AddTransactionBody) => c.post<AccountTransaction>('/transactions', body),
			remove: (id: string) => c.del(`/transactions/${id}`)
		},
		billSuggestions: {
			list: () => c.get<BillSuggestion[]>('/bill-suggestions'),
			accept: (
				id: string,
				overrides: Partial<
					Pick<BillSuggestion, 'name' | 'kind' | 'amountCents' | 'frequency' | 'nextDue'>
				> = {}
			) =>
				c.post<{ item: RecurringItem; matched: number }>(
					`/bill-suggestions/${id}/accept`,
					overrides
				),
			dismiss: (id: string) => c.post<void>(`/bill-suggestions/${id}/dismiss`)
		},
		matcher: {
			status: () => c.get<MatcherStatus>('/matcher/status'),
			rematch: () => c.post<{ queued: boolean }>('/inbox/rematch')
		},
		// ---- v5 (docs/API-CONTRACT.md § v5 additions)
		accounts: {
			list: () => c.get<Account[]>('/accounts'),
			transactions: (id: string, opts: AccountTxQuery = {}) =>
				c.get<AccountTransactionsPage>(`/accounts/${id}/transactions${accountTxQuery(opts)}`),
			// v7: setting `bills` moves the role off the previous bills account
			setRole: (id: string, role: AccountRole, ownerUserId: string | null = null) =>
				c.put<Account>(`/accounts/${id}/role`, { role, ownerUserId })
		},
		// ---- v7 (docs/API-CONTRACT.md § v7 additions)
		plan: {
			get: (date?: string | null) =>
				c.get<Plan>(`/plan${date ? `?date=${encodeURIComponent(date)}` : ''}`),
			sweepDone: (weekStart: string, actualCents?: number) =>
				c.post<Sweep>(
					'/plan/sweep/done',
					actualCents === undefined ? { weekStart } : { weekStart, actualCents }
				),
			sweepUndo: (weekStart: string) =>
				authFetch(event, '/plan/sweep/done', jsonInit('DELETE', { weekStart })).then(parse<Sweep>)
		},
		push: {
			/** Resolves to null when push is not configured on the server (503). */
			vapidPublicKey: async (): Promise<string | null> => {
				try {
					const r = await c.get<{ publicKey: string }>('/push/vapid-public-key');
					return r.publicKey;
				} catch (e) {
					if (isApiError(e, 503)) return null;
					throw e;
				}
			},
			prefs: () => c.get<PushPrefs>('/push/prefs'),
			updatePrefs: (body: PushPrefsBody) => c.put<PushPrefs>('/push/prefs', body),
			unsubscribe: (body: { endpoint?: string; id?: string }) =>
				authFetch(event, '/push/subscriptions', jsonInit('DELETE', body)).then(parse<void>),
			test: () => c.post<void>('/push/test')
		},
		passkeys: {
			list: () => c.get<Passkey[]>('/auth/passkeys'),
			remove: (id: string) => c.del(`/auth/passkeys/${id}`)
		},
		// ---- v3 (docs/API-CONTRACT.md § v3 additions)
		banks: {
			/** Resolves to null when bank sync is not configured on the server (503). */
			list: async (): Promise<BankLink[] | null> => {
				try {
					return await c.get<BankLink[]>('/banks');
				} catch (e) {
					if (isApiError(e, 503)) return null;
					throw e;
				}
			},
			claim: (setupToken: string, name?: string) =>
				c.post<BankLink>('/banks/simplefin/claim', name ? { setupToken, name } : { setupToken }),
			update: (id: string, patch: { name?: string; syncEnabled?: boolean }) =>
				c.patch<BankLink>(`/banks/${id}`, patch),
			remove: (id: string) => c.del(`/banks/${id}`),
			sync: (id: string) => c.post<SyncResult>(`/banks/${id}/sync`),
			map: (externalAccountId: string, body: ExternalAccountMapping) =>
				c.put<ExternalAccount>(`/banks/accounts/${externalAccountId}`, body)
		},
		inbox: {
			summary: () => c.get<InboxSummary>('/inbox/summary'),
			list: (
				opts: {
					status?: InboxStatus;
					before?: string | null;
					limit?: number;
					includeHidden?: boolean;
				} = {}
			) => {
				const q = new URLSearchParams();
				if (opts.status) q.set('status', opts.status);
				if (opts.includeHidden) q.set('includeHidden', '1');
				if (opts.before) q.set('before', opts.before);
				if (opts.limit) q.set('limit', String(opts.limit));
				const qs = q.toString();
				return c.get<InboxPage>(`/inbox${qs ? `?${qs}` : ''}`);
			},
			confirm: (id: string, body: InboxConfirmBody) =>
				c.post<InboxItem>(`/inbox/${id}/confirm`, body),
			ignore: (id: string) => c.post<InboxItem>(`/inbox/${id}/ignore`),
			restore: (id: string) => c.post<InboxItem>(`/inbox/${id}/restore`),
			// v5
			candidates: (id: string, days = 30) =>
				c.get<InboxCandidates>(`/inbox/${id}/candidates?days=${days}`),
			match: (id: string, itemId: string, dueDate: string) =>
				c.post<InboxItem>(`/inbox/${id}/match`, { itemId, dueDate }),
			transfer: (id: string, peerId: string | null) =>
				c.post<InboxItem>(`/inbox/${id}/transfer`, { peerId }),
			unmatch: (id: string) => c.post<InboxItem>(`/inbox/${id}/unmatch`),
			bulk: (action: 'confirm-suggested' | 'ignore', ids?: string[]) =>
				c.post<BulkResult>('/inbox/bulk', ids ? { action, ids } : { action })
		},
		imports: {
			csv: (body: { accountId: string; filename: string; rows: CsvImportRow[] }) =>
				c.post<CsvImportResult>('/imports/csv', body),
			listCsv: () => c.get<CsvImportRecord[]>('/imports/csv')
		}
	};
}

/** Unauthenticated GET (passkey config). */
export async function publicGet<T>(path: string): Promise<T> {
	const res = await fetch(`${API_BASE}${path}`, {
		headers: { accept: 'application/json, application/problem+json' }
	});
	return parse<T>(res);
}

/** `GET /auth/passkeys/config`, tolerant of old API builds and outages (→ disabled). */
export async function passkeyConfig(): Promise<PasskeyConfig> {
	try {
		return await publicGet<PasskeyConfig>('/auth/passkeys/config');
	} catch {
		return { enabled: false, rpId: '' };
	}
}

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
