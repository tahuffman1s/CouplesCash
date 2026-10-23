/**
 * Pay-period math, ported from the API contract so the settings page can preview the next
 * payday and the mock API can serve `/payperiod`:
 *   weekly   = 7 days from the anchor;      biweekly = 14 days
 *   monthly  = anchor day each month (clamped to the month's length)
 *   bimonthly = anchor day and anchor day + 15 (clamped); if anchor day > 15 the other payday is
 *               anchor day − 15
 *   start = most recent payday ≤ date, end = next payday − 1, nextPayday = end + 1.
 */
import type { PayPeriod } from '#lib/types.ts';
import { addDays, addMonths, daysInMonth, diffDays, parseISODate, toISODate } from './dates.ts';

export type PeriodBounds = { start: string; end: string; nextPayday: string; daysLeft: number };

function clampDay(year: number, monthIndex: number, day: number): string {
	return toISODate(new Date(year, monthIndex, Math.min(day, daysInMonth(year, monthIndex))));
}

/** Candidate paydays in the months around `date` for day-of-month based periods. */
function monthlyCandidates(period: PayPeriod, anchor: string, date: string): string[] {
	const anchorDay = parseISODate(anchor).getDate();
	const days = [anchorDay];
	if (period === 'bimonthly') days.push(anchorDay > 15 ? anchorDay - 15 : anchorDay + 15);
	const d = parseISODate(date);
	const out: string[] = [];
	for (let offset = -1; offset <= 1; offset++) {
		const m = addMonths(toISODate(new Date(d.getFullYear(), d.getMonth(), 1)), offset);
		const md = parseISODate(m);
		for (const day of days) out.push(clampDay(md.getFullYear(), md.getMonth(), day));
	}
	return [...new Set(out)].sort();
}

/** The pay period that contains `date` (YYYY-MM-DD). */
export function periodFor(period: PayPeriod, anchor: string, date: string): PeriodBounds {
	let start: string;
	let next: string;
	if (period === 'weekly' || period === 'biweekly') {
		const step = period === 'weekly' ? 7 : 14;
		const k = Math.floor(diffDays(anchor, date) / step);
		start = addDays(anchor, k * step);
		next = addDays(start, step);
	} else {
		const candidates = monthlyCandidates(period, anchor, date);
		start = candidates.filter((c) => c <= date).at(-1)!;
		next = candidates.find((c) => c > date)!;
	}
	return { start, end: addDays(next, -1), nextPayday: next, daysLeft: diffDays(date, next) };
}

/** Next payday strictly after `date`. */
export function nextPayday(period: PayPeriod, anchor: string, date: string): string {
	return periodFor(period, anchor, date).nextPayday;
}

/** 0..1 share of the period that has elapsed at `date`. */
export function periodProgress(bounds: { start: string; end: string }, date: string): number {
	const total = diffDays(bounds.start, bounds.end) + 1;
	const done = diffDays(bounds.start, date);
	return Math.min(1, Math.max(0, total > 0 ? done / total : 0));
}
