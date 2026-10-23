import { json } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import type { AuthResult } from '#lib/types.ts';
import { applyAuth, isApiError, publicPost } from '#lib/server/api.ts';
import { PROXY_HEADER, PROXY_HEADER_VALUE } from '../../../../hooks.server';

/**
 * Passkey sign-in BFF. Anonymous (the `/api` proxy requires a session), so it only forwards the
 * two WebAuthn login calls and turns the resulting AuthResult into the same httpOnly cookies the
 * password login sets. Requires the anti-CSRF marker header like the proxy does.
 */
function guard(request: Request): Response | null {
	if (request.headers.get(PROXY_HEADER) !== PROXY_HEADER_VALUE)
		return json({ title: 'Forbidden', status: 403 }, { status: 403 });
	return null;
}

/** Step 1: discoverable-credential request options. */
export const GET: RequestHandler = async ({ request }) => {
	const denied = guard(request);
	if (denied) return denied;
	try {
		const options = await publicPost<unknown>('/auth/passkeys/login/options', {});
		return json(options, { headers: { 'cache-control': 'no-store' } });
	} catch (e) {
		const status = isApiError(e) ? e.status : 503;
		return json({ title: 'Passkey sign-in unavailable', status }, { status });
	}
};

/** Step 2: verify the assertion, set the session cookies. */
export const POST: RequestHandler = async (event) => {
	const denied = guard(event.request);
	if (denied) return denied;
	let body: { challengeId?: string; credential?: unknown };
	try {
		body = (await event.request.json()) as typeof body;
	} catch {
		return json({ title: 'Bad request', status: 400 }, { status: 400 });
	}
	if (!body.challengeId || !body.credential)
		return json({ title: 'Bad request', status: 400 }, { status: 400 });
	try {
		const auth = await publicPost<AuthResult>('/auth/passkeys/login', {
			challengeId: body.challengeId,
			credential: body.credential
		});
		applyAuth(event, auth);
		return json(
			{ ok: true, hasHousehold: !!auth.household },
			{ headers: { 'cache-control': 'no-store' } }
		);
	} catch (e) {
		const status = isApiError(e) ? e.status : 503;
		return json(
			{ title: status === 401 ? 'That passkey was not recognised' : 'Sign-in failed', status },
			{ status }
		);
	}
};
