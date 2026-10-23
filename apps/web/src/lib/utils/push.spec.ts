import { describe, expect, it } from 'vitest';
import { urlBase64ToUint8Array } from './push.ts';

describe('urlBase64ToUint8Array', () => {
	it('decodes base64url without padding', () => {
		// "hello" → aGVsbG8 (base64url, no padding)
		expect([...urlBase64ToUint8Array('aGVsbG8')]).toEqual([104, 101, 108, 108, 111]);
	});
	it('maps - and _ back to + and /', () => {
		const bytes = Uint8Array.from([0xfb, 0xff, 0xbf]);
		const b64url = Buffer.from(bytes).toString('base64url');
		expect(b64url).toMatch(/[-_]/);
		expect([...urlBase64ToUint8Array(b64url)]).toEqual([...bytes]);
	});
	it('round-trips a 65-byte P-256 public key', () => {
		const key = Buffer.from(Array.from({ length: 65 }, (_, i) => (i * 37) % 256));
		expect(Buffer.from(urlBase64ToUint8Array(key.toString('base64url')))).toEqual(key);
	});
});
