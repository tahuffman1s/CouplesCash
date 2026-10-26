import { describe, expect, it } from 'vitest';
import {
	centsToInput,
	currencySymbol,
	flowTone,
	formatMoney,
	parseMoney,
	signedMoney
} from './money.ts';
import { currency } from '#lib/state/currency.svelte.ts';

describe('formatMoney', () => {
	it('formats cents as USD', () => {
		expect(formatMoney(123456)).toBe('$1,234.56');
		expect(formatMoney(0)).toBe('$0.00');
		expect(formatMoney(5)).toBe('$0.05');
	});
	it('handles negatives and signs', () => {
		expect(formatMoney(-250)).toBe('-$2.50');
		expect(formatMoney(250, { signed: true })).toBe('+$2.50');
		expect(formatMoney(0, { signed: true })).toBe('$0.00');
	});
	it('supports whole and compact styles', () => {
		expect(formatMoney(123456, { whole: true })).toBe('$1,235');
		expect(formatMoney(123456789, { compact: true })).toBe('$1.2M');
	});
	it('tolerates garbage', () => {
		expect(formatMoney(Number.NaN)).toBe('$0.00');
	});
	it('formats other currencies when asked', () => {
		expect(formatMoney(123456, { currency: 'EUR' })).toBe('€1,234.56');
		expect(formatMoney(123456, { currency: 'GBP', whole: true })).toBe('£1,235');
		// cents are always hundredths; zero-decimal currencies just drop the fraction
		expect(formatMoney(150000, { currency: 'JPY' })).toBe('¥1,500');
		expect(formatMoney(100, { currency: 'Z1' })).toBe('$1.00'); // malformed → USD fallback
	});
	it('follows the household currency by default', () => {
		currency.set('EUR');
		expect(formatMoney(250)).toBe('€2.50');
		expect(currencySymbol()).toBe('€');
		currency.set('usd');
		expect(formatMoney(250)).toBe('$2.50');
		expect(currencySymbol('GBP')).toBe('£');
	});
});

describe('parseMoney', () => {
	it('parses user input into cents', () => {
		expect(parseMoney('1,234.56')).toBe(123456);
		expect(parseMoney('$12')).toBe(1200);
		expect(parseMoney('€12,50'.replace(',', '.'))).toBe(1250);
		expect(parseMoney('0.5')).toBe(50);
		expect(parseMoney('-3')).toBe(-300);
		expect(parseMoney(' 7.1 ')).toBe(710);
	});
	it('rejects invalid input', () => {
		expect(parseMoney('')).toBeNull();
		expect(parseMoney('abc')).toBeNull();
		expect(parseMoney('1.234')).toBeNull();
		expect(parseMoney(null)).toBeNull();
	});
	it('round-trips through centsToInput', () => {
		expect(centsToInput(123456)).toBe('1234.56');
		expect(parseMoney(centsToInput(99))).toBe(99);
	});
});

describe('signedMoney (v6 flows)', () => {
	it('signs money out and in, leaves zero alone', () => {
		expect(signedMoney(-1249)).toBe('−$12.49');
		expect(signedMoney(320000)).toBe('+$3,200.00');
		expect(signedMoney(0)).toBe('$0.00');
		expect(signedMoney(-185000, { whole: true })).toBe('−$1,850');
	});
	it('colours by direction', () => {
		expect(flowTone(-1)).toBe('text-error');
		expect(flowTone(1)).toBe('text-success');
		expect(flowTone(0)).toBe('');
	});
});
