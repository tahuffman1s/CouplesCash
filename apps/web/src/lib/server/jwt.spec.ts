import { describe, expect, it } from 'vitest';
import { decodeJwt } from './jwt.ts';

function token(payload: Record<string, unknown>): string {
	const enc = (o: unknown) => Buffer.from(JSON.stringify(o)).toString('base64url');
	return `${enc({ alg: 'HS256', typ: 'JWT' })}.${enc(payload)}.sig`;
}

describe('decodeJwt', () => {
	const exp = Math.floor(Date.now() / 1000) + 600;

	it('decodes the claims we need', () => {
		const user = decodeJwt(token({ sub: 'u1', email: 'a@b.c', name: 'Alex', hh: 'h1', exp }));
		expect(user).toEqual({ id: 'u1', email: 'a@b.c', name: 'Alex', household: 'h1', exp });
	});

	it('treats a missing hh claim as no household', () => {
		expect(decodeJwt(token({ sub: 'u1', exp }))?.household).toBeNull();
	});

	it('rejects expired tokens', () => {
		expect(decodeJwt(token({ sub: 'u1', exp: exp - 1200 }))).toBeNull();
	});

	it('rejects malformed tokens', () => {
		expect(decodeJwt('')).toBeNull();
		expect(decodeJwt('a.b')).toBeNull();
		expect(decodeJwt('a.!!!.c')).toBeNull();
		expect(decodeJwt(token({ exp }))).toBeNull(); // no sub
		expect(decodeJwt(undefined)).toBeNull();
	});
});
