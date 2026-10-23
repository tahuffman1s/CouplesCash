import { describe, expect, it } from 'vitest';
import {
	buildRows,
	detectDateFormat,
	detectHeader,
	guessMapping,
	parseAmount,
	parseDate,
	rowAmount
} from './csv';

describe('parseAmount', () => {
	it('parses thousands separators and decimals', () => {
		expect(parseAmount('1,234.56')).toBe(123456);
		expect(parseAmount('12.5')).toBe(1250);
		expect(parseAmount('7')).toBe(700);
		expect(parseAmount('1,234')).toBe(123400);
	});
	it('treats parentheses, unicode minus and currency symbols as sign/noise', () => {
		expect(parseAmount('(12.34)')).toBe(-1234);
		expect(parseAmount('$-5.00')).toBe(-500);
		expect(parseAmount('-$5.00')).toBe(-500);
		expect(parseAmount('−5.00')).toBe(-500);
		expect(parseAmount('€ 12,34')).toBe(1234);
		expect(parseAmount('1.234,56')).toBe(123456);
		expect(parseAmount('5.00-')).toBe(-500);
		expect(parseAmount('+20.00')).toBe(2000);
	});
	it('returns null for blanks and words', () => {
		expect(parseAmount('')).toBeNull();
		expect(parseAmount('   ')).toBeNull();
		expect(parseAmount('Amount')).toBeNull();
		expect(parseAmount(null)).toBeNull();
		expect(parseAmount('-')).toBeNull();
	});
});

describe('dates', () => {
	it('parses each supported format', () => {
		expect(parseDate('2026-10-03', 'iso')).toBe('2026-10-03');
		expect(parseDate('2026-10-03T14:00:00Z', 'iso')).toBe('2026-10-03');
		expect(parseDate('10/03/2026', 'mdy')).toBe('2026-10-03');
		expect(parseDate('3/10/2026', 'dmy')).toBe('2026-10-03');
		expect(parseDate('10/03/26', 'mdy')).toBe('2026-10-03');
		expect(parseDate('Oct 3, 2026', 'text')).toBe('2026-10-03');
		expect(parseDate('3 October 2026', 'text')).toBe('2026-10-03');
		expect(parseDate('13/13/2026', 'mdy')).toBeNull();
		expect(parseDate('nope', 'iso')).toBeNull();
	});
	it('detects the format from samples, resolving slash ambiguity with a day > 12', () => {
		expect(detectDateFormat(['2026-10-03', '2026-10-04'])).toBe('iso');
		expect(detectDateFormat(['10/03/2026', '10/21/2026'])).toBe('mdy');
		expect(detectDateFormat(['03/10/2026', '21/10/2026'])).toBe('dmy');
		expect(detectDateFormat(['01/02/2026'])).toBe('mdy');
		expect(detectDateFormat(['Oct 3, 2026', 'Nov 1, 2026'])).toBe('text');
		expect(detectDateFormat(['hello'])).toBeNull();
		expect(detectDateFormat([])).toBeNull();
	});
});

describe('header detection and mapping', () => {
	const headed = [
		['Date', 'Description', 'Debit', 'Credit'],
		['10/03/2026', 'KROGER #123', '12.49', ''],
		['10/05/2026', 'ACME PAYROLL', '', '3200.00']
	];
	it('spots a header row', () => {
		expect(detectHeader(headed)).toBe(true);
		expect(detectHeader(headed.slice(1))).toBe(false);
	});
	it('maps by header names, including debit/credit pairs', () => {
		const m = guessMapping(headed[0], headed.slice(1));
		expect(m).toMatchObject({ date: 0, description: 1, debit: 2, credit: 3, amount: null });
	});
	it('maps by sample values when there is no header', () => {
		const rows = [
			['2026-10-03', '-12.49', 'KROGER #123'],
			['2026-10-05', '3200.00', 'ACME PAYROLL']
		];
		const m = guessMapping(null, rows);
		expect(m).toMatchObject({ date: 0, amount: 1, description: 2 });
	});
	it('recognises payee and memo columns', () => {
		const m = guessMapping(
			['Posted', 'Payee', 'Memo', 'Amount'],
			[['2026-10-03', 'Kroger', 'groceries', '-12.49']]
		);
		expect(m).toMatchObject({ date: 0, payee: 1, memo: 2, amount: 3, description: 1 });
	});
});

describe('rowAmount / buildRows', () => {
	const mapping = {
		date: 0,
		description: 1,
		amount: null,
		debit: 2,
		credit: 3,
		payee: null,
		memo: null
	};
	it('merges debit and credit into one signed amount', () => {
		expect(rowAmount(['', '', '12.49', ''], mapping, true)).toBe(-1249);
		expect(rowAmount(['', '', '', '3200.00'], mapping, true)).toBe(320000);
		expect(rowAmount(['', '', '-12.49', ''], mapping, true)).toBe(-1249);
		expect(rowAmount(['', '', '', ''], mapping, true)).toBeNull();
	});
	it('honours the sign convention for a single amount column', () => {
		const single = { ...mapping, amount: 2, debit: null, credit: null };
		expect(rowAmount(['', '', '-12.49'], single, true)).toBe(-1249);
		expect(rowAmount(['', '', '12.49'], single, false)).toBe(-1249);
	});
	it('drops the header plus rows without a date or amount and converts the rest', () => {
		const rows = [
			['Date', 'Description', 'Debit', 'Credit'],
			['10/03/2026', 'KROGER #123', '12.49', ''],
			['', 'balance carried', '', ''],
			['10/05/2026', 'ACME PAYROLL', '', '3,200.00'],
			['10/06/2026', 'ZERO', '0', ''],
			[]
		];
		const out = buildRows(rows, {
			mapping,
			dateFormat: 'mdy',
			outflowsNegative: true,
			hasHeader: true
		});
		expect(out.rows).toEqual([
			{ postedOn: '2026-10-03', amountCents: -1249, description: 'KROGER #123' },
			{ postedOn: '2026-10-05', amountCents: 320000, description: 'ACME PAYROLL' }
		]);
		expect(out.skipped).toBe(2);
		expect(out.total).toBe(4);
	});
});
