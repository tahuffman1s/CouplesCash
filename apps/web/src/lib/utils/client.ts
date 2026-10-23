import type { Problem } from '#lib/types.ts';

export class ClientApiError extends Error {
	readonly status: number;
	readonly problem: Problem;
	/** Seconds from a `Retry-After` header (429), when the server sent one. */
	readonly retryAfterSeconds: number | null;
	constructor(status: number, problem: Problem, retryAfterSeconds: number | null = null) {
		super(problem.detail || problem.title);
		this.status = status;
		this.problem = problem;
		this.retryAfterSeconds = retryAfterSeconds;
	}
}

/**
 * Browser-side fetch through the BFF proxy (`/api/...`). Adds the anti-CSRF marker header the
 * proxy requires and unwraps JSON / problem+json.
 */
export async function apiFetch<T>(path: string, init: RequestInit = {}): Promise<T> {
	const headers = new Headers(init.headers);
	headers.set('X-Requested-With', 'CouplesCash');
	headers.set('accept', 'application/json');
	if (init.body !== undefined && !headers.has('content-type')) {
		headers.set('content-type', 'application/json');
	}
	const res = await fetch(`/api${path.startsWith('/') ? path : `/${path}`}`, {
		...init,
		headers,
		credentials: 'same-origin'
	});
	if (res.status === 401) {
		// session is gone – a full navigation lets the server redirect to /login
		window.location.assign('/login');
		throw new ClientApiError(401, { title: 'Unauthorized', status: 401 });
	}
	if (!res.ok) {
		let problem: Problem = { title: res.statusText || 'Request failed', status: res.status };
		try {
			problem = { ...problem, ...((await res.json()) as Partial<Problem>) };
		} catch {
			// not json
		}
		const retry = res.headers.get('retry-after');
		const seconds = retry && /^\d+$/.test(retry) ? Number(retry) : null;
		throw new ClientApiError(res.status, problem, seconds);
	}
	if (res.status === 204) return undefined as T;
	return (await res.json()) as T;
}
