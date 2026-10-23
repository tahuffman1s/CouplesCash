export type JwtUser = {
	id: string;
	email: string;
	name: string;
	household: string | null;
	/** Unix seconds. */
	exp: number;
};

function base64UrlDecode(input: string): string {
	const b64 = input.replace(/-/g, '+').replace(/_/g, '/');
	const padded = b64 + '='.repeat((4 - (b64.length % 4)) % 4);
	return Buffer.from(padded, 'base64').toString('utf8');
}

/**
 * Decodes the payload of a JWT *without* verifying the signature.
 * The API verifies every request; the BFF only needs the claims for routing and display.
 * Returns null for malformed tokens or tokens past their `exp`.
 */
export function decodeJwt(token: string | undefined | null, now = Date.now()): JwtUser | null {
	if (!token) return null;
	const parts = token.split('.');
	if (parts.length !== 3) return null;
	try {
		const payload = JSON.parse(base64UrlDecode(parts[1])) as Record<string, unknown>;
		const sub = typeof payload.sub === 'string' ? payload.sub : null;
		const exp = typeof payload.exp === 'number' ? payload.exp : null;
		if (!sub || exp === null) return null;
		if (exp * 1000 <= now) return null;
		return {
			id: sub,
			email: typeof payload.email === 'string' ? payload.email : '',
			name: typeof payload.name === 'string' ? payload.name : '',
			household: typeof payload.hh === 'string' && payload.hh ? payload.hh : null,
			exp
		};
	} catch {
		return null;
	}
}
