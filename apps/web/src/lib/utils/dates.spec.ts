import { describe, expect, it } from 'vitest';
import {
	addDays,
	addMonths,
	diffDays,
	endOfMonth,
	isSameDay,
	isValidISODate,
	parseISODate,
	relativeDay,
	startOfMonth,
	toISODate,
	weekday
} from './dates.ts';

describe('dates', () => {
	it('round-trips local dates without UTC shifting', () => {
		const d = new Date(2026, 0, 1, 0, 30); // local midnight-ish on Jan 1
		expect(toISODate(d)).toBe('2026-01-01');
		expect(parseISODate('2026-01-01').getDate()).toBe(1);
		expect(parseISODate('2026-01-01').getHours()).toBe(0);
	});

	it('validates ISO dates', () => {
		expect(isValidISODate('2026-02-29')).toBe(false);
		expect(isValidISODate('2024-02-29')).toBe(true);
		expect(isValidISODate('2026-13-01')).toBe(false);
		expect(isValidISODate('nope')).toBe(false);
	});

	it('adds months and clamps to month end', () => {
		expect(addMonths('2026-01-31', 1)).toBe('2026-02-28');
		expect(addMonths('2024-01-31', 1)).toBe('2024-02-29');
		expect(addMonths('2026-03-31', -1)).toBe('2026-02-28');
		expect(addMonths('2026-11-15', 2)).toBe('2027-01-15');
		expect(addMonths('2026-01-15', -1)).toBe('2025-12-15');
	});

	it('computes month boundaries', () => {
		expect(startOfMonth('2026-10-23')).toBe('2026-10-01');
		expect(endOfMonth('2026-10-23')).toBe('2026-10-31');
		expect(endOfMonth('2024-02-10')).toBe('2024-02-29');
	});

	it('adds days across DST and year boundaries', () => {
		expect(addDays('2026-12-31', 1)).toBe('2027-01-01');
		expect(addDays('2026-03-08', 1)).toBe('2026-03-09');
		expect(diffDays('2026-01-01', '2026-01-31')).toBe(30);
	});

	it('compares days', () => {
		expect(isSameDay('2026-05-05', new Date(2026, 4, 5, 23, 59))).toBe(true);
		expect(isSameDay('2026-05-05', '2026-05-06')).toBe(false);
		expect(weekday('2026-10-23')).toBe(5); // Friday
	});

	it('formats relative days', () => {
		expect(relativeDay('2026-10-23', '2026-10-23')).toBe('Today');
		expect(relativeDay('2026-10-24', '2026-10-23')).toBe('Tomorrow');
		expect(relativeDay('2026-10-26', '2026-10-23')).toBe('In 3 days');
		expect(relativeDay('2026-10-21', '2026-10-23')).toBe('2 days ago');
	});
});
