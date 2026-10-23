import { describe, expect, it } from 'vitest';
import { nextPayday, periodFor, periodProgress } from './payperiod.ts';

describe('pay period math', () => {
	it('weekly / biweekly step from the anchor', () => {
		expect(periodFor('weekly', '2026-10-02', '2026-10-23')).toEqual({
			start: '2026-10-23',
			end: '2026-10-29',
			nextPayday: '2026-10-30',
			daysLeft: 7
		});
		expect(periodFor('biweekly', '2026-10-02', '2026-10-23')).toEqual({
			start: '2026-10-16',
			end: '2026-10-29',
			nextPayday: '2026-10-30',
			daysLeft: 7
		});
		// an anchor in the future still defines the cadence
		expect(nextPayday('biweekly', '2026-11-13', '2026-10-23')).toBe('2026-10-30');
	});

	it('monthly clamps the anchor day to short months', () => {
		expect(periodFor('monthly', '2026-01-31', '2026-02-10')).toEqual({
			start: '2026-01-31',
			end: '2026-02-27',
			nextPayday: '2026-02-28',
			daysLeft: 18
		});
		expect(nextPayday('monthly', '2026-01-15', '2026-10-23')).toBe('2026-11-15');
		// on payday itself a new period starts
		expect(periodFor('monthly', '2026-01-15', '2026-10-15').start).toBe('2026-10-15');
	});

	it('bimonthly pays on the anchor day and +15 (or −15 when the anchor is late)', () => {
		expect(nextPayday('bimonthly', '2026-01-01', '2026-10-23')).toBe('2026-11-01');
		expect(periodFor('bimonthly', '2026-01-01', '2026-10-20').start).toBe('2026-10-16');
		// anchor on the 20th → other payday on the 5th
		expect(periodFor('bimonthly', '2026-01-20', '2026-10-10')).toEqual({
			start: '2026-10-05',
			end: '2026-10-19',
			nextPayday: '2026-10-20',
			daysLeft: 10
		});
		// anchor on the 31st → paydays 31st (clamped) and 16th
		expect(nextPayday('bimonthly', '2026-01-31', '2026-02-20')).toBe('2026-02-28');
	});

	it('reports elapsed progress', () => {
		const b = { start: '2026-10-01', end: '2026-10-10' };
		expect(periodProgress(b, '2026-10-01')).toBe(0);
		expect(periodProgress(b, '2026-10-06')).toBeCloseTo(0.5);
		expect(periodProgress(b, '2026-10-20')).toBe(1);
	});
});
