/**
 * Week-view helpers (pure, unit-tested): week bounds, range labels, variance and budget
 * wording. The API computes the numbers; these only shape them for the screen.
 */
import type { Kind, WeekDay } from '#lib/types.ts';
import { addDays, gridColumn, parseISODate, today, type ISODate } from './dates.ts';

/** First day of the week that contains `iso` for a household whose week starts on Sun (0) / Mon (1). */
export function weekStartFor(iso: ISODate, weekStart: 0 | 1): ISODate {
	return addDays(iso, -gridColumn(iso, weekStart));
}

/** Last day (inclusive) of the week starting on `start`. */
export function weekEnd(start: ISODate): ISODate {
	return addDays(start, 6);
}

const monthDay = new Intl.DateTimeFormat('en-US', { month: 'short', day: 'numeric' });
const monthDayYear = new Intl.DateTimeFormat('en-US', {
	month: 'short',
	day: 'numeric',
	year: 'numeric'
});

/**
 * "Oct 20 – 26" within one month, "Oct 27 – Nov 2" across months, and the year is appended
 * when the week ends in a different year from `now` ("Dec 29 – Jan 4, 2027").
 */
export function weekRangeLabel(start: ISODate, end: ISODate, now: ISODate = today()): string {
	const a = parseISODate(start);
	const b = parseISODate(end);
	const sameYear = b.getFullYear() === parseISODate(now).getFullYear();
	const last = sameYear ? monthDay.format(b) : monthDayYear.format(b);
	if (a.getMonth() === b.getMonth() && a.getFullYear() === b.getFullYear()) {
		return `${monthDay.format(a)} – ${sameYear ? b.getDate() : `${b.getDate()}, ${b.getFullYear()}`}`;
	}
	return `${monthDay.format(a)} – ${last}`;
}

/**
 * "+$4.20" when it cost more than planned, "−$5.80" (U+2212) when less; null when on target
 * or unknown. `format` is injected so the helper stays free of the household-currency state.
 */
export function formatVariance(
	cents: number | null | undefined,
	format: (c: number) => string
): string | null {
	if (cents === null || cents === undefined || cents === 0 || !Number.isFinite(cents)) return null;
	return `${cents > 0 ? '+' : '−'}${format(Math.abs(cents))}`;
}

/**
 * Whether a variance is good news: a bill that cost less, or income that paid more.
 * Returns null for zero / unknown.
 */
export function varianceTone(
	cents: number | null | undefined,
	kind: Kind
): 'success' | 'error' | null {
	if (cents === null || cents === undefined || cents === 0) return null;
	const good = kind === 'income' ? cents > 0 : cents < 0;
	return good ? 'success' : 'error';
}

/** Budget status wording: "under by $X" (success) or "over by $X" (error); exact → "on target". */
export function budgetStatus(
	spentCents: number,
	budgetCents: number,
	format: (c: number) => string
): { label: string; tone: 'success' | 'error' | 'neutral'; over: boolean } {
	const diff = spentCents - budgetCents;
	if (diff > 0) return { label: `over by ${format(diff)}`, tone: 'error', over: true };
	if (diff < 0) return { label: `under by ${format(-diff)}`, tone: 'success', over: false };
	return { label: 'on target', tone: 'neutral', over: false };
}

/** Percent of a budget spent, capped at 100 for the bar (0 when the budget is 0 and nothing spent). */
export function cappedPercent(spentCents: number, budgetCents: number): number {
	if (budgetCents <= 0) return spentCents > 0 ? 100 : 0;
	return Math.min(100, Math.max(0, (spentCents / budgetCents) * 100));
}

/**
 * Default date for "Plan a one-off" from the week view: the first day of the viewed week that is
 * today or later; falls back to the week's first day for past weeks.
 */
export function firstFutureDay(days: Pick<WeekDay, 'date'>[], now: ISODate = today()): ISODate {
	const hit = days.find((d) => d.date >= now);
	return hit?.date ?? days[0]?.date ?? now;
}

/** Short tick label for the history chart: "10/6". */
export function shortWeekLabel(iso: ISODate): string {
	const d = parseISODate(iso);
	return `${d.getMonth() + 1}/${d.getDate()}`;
}
