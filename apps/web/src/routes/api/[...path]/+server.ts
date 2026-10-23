import type { RequestHandler } from './$types';
import { authFetch } from '#lib/server/api.ts';

/**
 * Thin authenticated proxy for client-side fetches (calendar navigation, mark paid, balance
 * updates). `hooks.server.ts` already enforced the `X-Requested-With: CouplesCash` header and a
 * valid session before this handler runs.
 */
const HOP_BY_HOP = new Set([
	'connection',
	'keep-alive',
	'proxy-authenticate',
	'proxy-authorization',
	'proxy-connection',
	'te',
	'trailer',
	'transfer-encoding',
	'upgrade',
	'host',
	'cookie',
	'set-cookie',
	'authorization',
	'content-length'
]);

const handler: RequestHandler = async (event) => {
	const { request, params, url } = event;
	const method = request.method.toUpperCase();

	const headers = new Headers();
	for (const [name, value] of request.headers) {
		const lower = name.toLowerCase();
		if (HOP_BY_HOP.has(lower)) continue;
		if (lower === 'content-type' || lower === 'accept' || lower === 'accept-language') {
			headers.set(name, value);
		}
	}

	const hasBody = method !== 'GET' && method !== 'HEAD';
	const body = hasBody ? await request.arrayBuffer() : undefined;

	const upstream = await authFetch(
		event,
		`/${params.path}${url.search}`,
		{ method, headers, body: body && body.byteLength > 0 ? body : undefined },
		'return'
	);

	const out = new Headers();
	for (const [name, value] of upstream.headers) {
		if (!HOP_BY_HOP.has(name.toLowerCase())) out.set(name, value);
	}
	out.set('cache-control', 'no-store');

	return new Response(upstream.status === 204 ? null : upstream.body, {
		status: upstream.status,
		headers: out
	});
};

export const GET = handler;
export const POST = handler;
export const PUT = handler;
export const PATCH = handler;
export const DELETE = handler;
