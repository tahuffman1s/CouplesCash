import { describe, expect, it } from 'vitest';
import { normaliseCoupleCode } from './couple-code.ts';

describe('normaliseCoupleCode', () => {
	it('normalises typed codes', () => {
		expect(normaliseCoupleCode('abcd-efgh')).toBe('ABCD-EFGH');
		expect(normaliseCoupleCode(' abcdefgh ')).toBe('ABCD-EFGH');
		expect(normaliseCoupleCode('ABCD EFGH')).toBe('ABCD-EFGH');
	});
	it('passes payloads through and unwraps deep links', () => {
		const payload = 'cc1.' + 'A'.repeat(27);
		expect(normaliseCoupleCode(payload)).toBe(payload);
		expect(
			normaliseCoupleCode(`https://cash.example.com/couple/join?c=${encodeURIComponent(payload)}`)
		).toBe(payload);
	});
	it('rejects junk', () => {
		expect(normaliseCoupleCode('')).toBeNull();
		expect(normaliseCoupleCode('abc')).toBeNull();
		expect(normaliseCoupleCode('cc1.!!')).toBeNull();
		expect(normaliseCoupleCode('https://example.com/')).toBeNull();
	});
});
