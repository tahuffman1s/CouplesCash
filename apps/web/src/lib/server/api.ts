import { API_URL } from '$app/env/private';
import { redirect, type RequestEvent } from '@sveltejs/kit';
import type {
	AccountTransactionsPage,
	AccountType,
	BillPayment,
	FromTransactionResult,
	InboxCandidates,
	Occurrence,
	TransferReport,
	ActivityPage,
	AuthResult,
	BankLink,
	BudgetRow,
	BudgetsResponse,
	BulkResult,
	CsvImportResult,
	CsvImportRecord,
	CsvImportRow,
	Expense,
	ExternalAccount,
	Frequency,
	Goal,
	Household,
	Kind,
	InboxItem,
	InboxPage,
	InboxStatus,
	InboxSummary,
	MonthReport,
	Passkey,
	PasskeyConfig,
	PayPeriod,
	PayPeriodView,
	Problem,
	PushPrefs,
	RecurringItem,
	SyncResult,
	Todo,
	WeekBudget,
	WeekBudgetRow,
	WeekView
} from '#lib/types.ts';
import { accountTxQuery, type AccountTxQuery } from '#lib/utils/transactions.ts';
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

export type HouseholdPatch = Partial<{
	name: string;
	currency: string;
	weekStart: 0 | 1;
	timezone: string;
	defaultAccountId: string | null;
	payPeriod: PayPeriod;
	payAnchorDate: string | null;
	weeklySpendCents: number | null;
	autoIgnoreTransfers: boolean;
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
	payeeHint: string | null;
};
export type ExpenseBody = {
	name: string;
	amountCents: number;
	categoryId: string | null;
	accountId: string | null;
	spentOn: string;
	note: string | null;
	deduct: boolean;
};
export type TodoBody = { title: string; amountCents: number | null; dueDate: string | null };
export type GoalBody = {
	name: string;
	targetCents: number;
	targetDate: string | null;
	accountId: string | null;
	color: string;
	icon: string;
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
	const month = (m?: string | null) => (m ? `?month=${encodeURIComponent(m)}` : '');
	return {
		household: {
			get: () => c.get<Household | null>('/couple'),
			update: (patch: HouseholdPatch) => c.patch<Household>('/couple', patch)
		},
		activity: {
			list: (opts: { before?: string | null; limit?: number } = {}) => {
				const q = new URLSearchParams();
				if (opts.before) q.set('before', opts.before);
				if (opts.limit) q.set('limit', String(opts.limit));
				const qs = q.toString();
				return c.get<ActivityPage>(`/activity${qs ? `?${qs}` : ''}`);
			}
		},
		expenses: {
			list: (m?: string | null) => c.get<Expense[]>(`/expenses${month(m)}`),
			range: (from: string, to: string) => c.get<Expense[]>(`/expenses?from=${from}&to=${to}`),
			get: (id: string) => c.get<Expense>(`/expenses/${id}`),
			create: (body: ExpenseBody) => c.post<Expense>('/expenses', body),
			update: (id: string, body: ExpenseBody) => c.put<Expense>(`/expenses/${id}`, body),
			remove: (id: string) => c.del(`/expenses/${id}`)
		},
		budgets: {
			list: (m?: string | null) => c.get<BudgetsResponse>(`/budgets${month(m)}`),
			set: (categoryId: string, amountCents: number) =>
				c.put<BudgetRow>(`/budgets/${categoryId}`, { amountCents }),
			clear: (categoryId: string) => c.del(`/budgets/${categoryId}`),
			// v4: weekly rows live beside the monthly ones (`?period=weekly`)
			week: (start?: string | null) =>
				c.get<WeekBudget>(`/budgets/week${start ? `?start=${encodeURIComponent(start)}` : ''}`),
			setWeekly: (categoryId: string, amountCents: number) =>
				c.put<WeekBudgetRow>(`/budgets/${categoryId}?period=weekly`, { amountCents }),
			clearWeekly: (categoryId: string) => c.del(`/budgets/${categoryId}?period=weekly`)
		},
		recurring: {
			create: (body: RecurringBody) => c.post<RecurringItem>('/recurring', body),
			update: (id: string, body: RecurringBody) => c.put<RecurringItem>(`/recurring/${id}`, body),
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
				c.post<Occurrence>(`/recurring/${id}/occurrences/${date}/link`, { transactionId })
		},
		// ---- v5 (docs/API-CONTRACT.md § v5 additions)
		accounts: {
			transactions: (id: string, opts: AccountTxQuery = {}) =>
				c.get<AccountTransactionsPage>(`/accounts/${id}/transactions${accountTxQuery(opts)}`)
		},
		transfers: {
			report: (m?: string | null) => c.get<TransferReport>(`/transfers${month(m)}`)
		},
		// ---- v4 (docs/API-CONTRACT.md § v4 additions)
		week: {
			view: (start?: string | null) =>
				c.get<WeekView>(`/week${start ? `?start=${encodeURIComponent(start)}` : ''}`)
		},
		payperiod: {
			/** Resolves to null when the household has not configured a pay period (409). */
			view: async (date?: string | null): Promise<PayPeriodView | null> => {
				try {
					return await c.get<PayPeriodView>(`/payperiod${date ? `?date=${date}` : ''}`);
				} catch (e) {
					if (isApiError(e, 409)) return null;
					throw e;
				}
			}
		},
		todos: {
			list: (includeDone = false) =>
				c.get<Todo[]>(`/todos${includeDone ? '?includeDone=true' : ''}`),
			create: (body: TodoBody) => c.post<Todo>('/todos', body),
			update: (id: string, body: TodoBody & { sortOrder?: number }) =>
				c.put<Todo>(`/todos/${id}`, body),
			done: (id: string) => c.post<Todo>(`/todos/${id}/done`),
			undone: (id: string) => c.del<Todo>(`/todos/${id}/done`),
			remove: (id: string) => c.del(`/todos/${id}`),
			order: (ids: string[]) => c.put<void>('/todos/order', { ids })
		},
		goals: {
			list: () => c.get<Goal[]>('/goals'),
			get: (id: string) => c.get<Goal>(`/goals/${id}`),
			create: (body: GoalBody) => c.post<Goal>('/goals', body),
			update: (id: string, body: GoalBody) => c.put<Goal>(`/goals/${id}`, body),
			remove: (id: string) => c.del(`/goals/${id}`),
			contribute: (id: string, amountCents: number) =>
				c.post<Goal>(`/goals/${id}/contribute`, { amountCents })
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
		reports: {
			month: (m?: string | null) => c.get<MonthReport>(`/reports/month${month(m)}`)
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
