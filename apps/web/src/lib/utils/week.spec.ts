import { describe, expect, it } from 'vitest';
import {
	budgetStatus,
	cappedPercent,
	firstFutureDay,
	formatVariance,
	shortWeekLabel,
	varianceTone,
	weekEnd,
	weekRangeLabel,
	weekStartFor
} from './week.ts';

const usd = (c: number) => `$${(c / 100).toFixed(2)}`;

describe('week bounds', () => {
	it('finds the Sunday or Monday that starts the week', () => {
		// 2026-10-25 is a Sunday
		expect(weekStartFor('2026-10-25', 0)).toBe('2026-10-25');
		expect(weekStartFor('2026-10-25', 1)).toBe('2026-10-19');
		expect(weekStartFor('2026-10-28', 0)).toBe('2026-10-25');
		expect(weekStartFor('2026-10-28', 1)).toBe('2026-10-26');
		expect(weekEnd('2026-10-19')).toBe('2026-10-25');
	});
});

describe('weekRangeLabel', () => {
	it('collapses the month when the week stays inside it', () => {
		expect(weekRangeLabel('2026-10-20', '2026-10-26', '2026-10-25')).toBe('Oct 20 – 26');
	});
	it('names both months when the week crosses one', () => {
		expect(weekRangeLabel('2026-10-27', '2026-11-02', '2026-10-25')).toBe('Oct 27 – Nov 2');
	});
	it('adds the year when the week ends in another year', () => {
		expect(weekRangeLabel('2026-12-29', '2027-01-04', '2026-10-25')).toBe('Dec 29 – Jan 4, 2027');
		expect(weekRangeLabel('2027-01-05', '2027-01-11', '2026-10-25')).toBe('Jan 5 – 11, 2027');
	});
});

describe('variance', () => {
	it('formats with an explicit sign and a real minus', () => {
		expect(formatVariance(420, usd)).toBe('+$4.20');
		expect(formatVariance(-580, usd)).toBe('−$5.80');
		expect(formatVariance(0, usd)).toBeNull();
		expect(formatVariance(null, usd)).toBeNull();
		expect(formatVariance(undefined, usd)).toBeNull();
	});
	it('reads a cheaper bill and a bigger paycheck as good news', () => {
		expect(varianceTone(-580, 'bill')).toBe('success');
		expect(varianceTone(420, 'bill')).toBe('error');
		expect(varianceTone(420, 'income')).toBe('success');
		expect(varianceTone(-100, 'income')).toBe('error');
		expect(varianceTone(0, 'bill')).toBeNull();
	});
});

describe('budgetStatus', () => {
	it('words under / over / on target', () => {
		expect(budgetStatus(4500, 30000, usd)).toEqual({
			label: 'under by $255.00',
			tone: 'success',
			over: false
		});
		expect(budgetStatus(7000, 5000, usd)).toEqual({
			label: 'over by $20.00',
			tone: 'error',
			over: true
		});
		expect(budgetStatus(5000, 5000, usd).label).toBe('on target');
	});
	it('caps the bar at 100 %', () => {
		expect(cappedPercent(4500, 12000)).toBe(37.5);
		expect(cappedPercent(7000, 5000)).toBe(100);
		expect(cappedPercent(0, 0)).toBe(0);
		expect(cappedPercent(100, 0)).toBe(100);
	});
});

describe('firstFutureDay', () => {
	const days = ['2026-10-19', '2026-10-20', '2026-10-21'].map((date) => ({ date }));
	it('picks today or the next day of the viewed week', () => {
		expect(firstFutureDay(days, '2026-10-20')).toBe('2026-10-20');
		expect(firstFutureDay(days, '2026-10-01')).toBe('2026-10-19');
	});
	it('falls back to the first day for past weeks', () => {
		expect(firstFutureDay(days, '2026-11-01')).toBe('2026-10-19');
		expect(firstFutureDay([], '2026-11-01')).toBe('2026-11-01');
	});
	it('labels weeks compactly for the history axis', () => {
		expect(shortWeekLabel('2026-10-05')).toBe('10/5');
	});
});
