/**
 * Date helpers built around `YYYY-MM-DD` strings interpreted in *local* time.
 * Never `new Date('YYYY-MM-DD')` — that parses as UTC and shifts the day.
 */

export type ISODate = string; // YYYY-MM-DD

const ISO_RE = /^(\d{4})-(\d{2})-(\d{2})$/;

function pad(n: number): string {
	return n < 10 ? `0${n}` : String(n);
}

/** Local-time Date → YYYY-MM-DD. */
export function toISODate(d: Date): ISODate {
	return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

/** YYYY-MM-DD → Date at local midnight. Throws on malformed input. */
export function parseISODate(iso: ISODate): Date {
	const m = ISO_RE.exec(iso);
	if (!m) throw new Error(`Invalid ISO date: ${iso}`);
	return new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3]));
}

export function isValidISODate(iso: string): boolean {
	const m = ISO_RE.exec(iso);
	if (!m) return false;
	const d = new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3]));
	return toISODate(d) === iso;
}

export function today(): ISODate {
	return toISODate(new Date());
}

export function daysInMonth(year: number, monthIndex: number): number {
	return new Date(year, monthIndex + 1, 0).getDate();
}

export function startOfMonth(iso: ISODate): ISODate {
	return `${iso.slice(0, 7)}-01`;
}

export function endOfMonth(iso: ISODate): ISODate {
	const d = parseISODate(iso);
	return `${iso.slice(0, 7)}-${pad(daysInMonth(d.getFullYear(), d.getMonth()))}`;
}

/** Adds months, clamping the day to the target month's length (Jan 31 + 1 → Feb 28/29). */
export function addMonths(iso: ISODate, n: number): ISODate {
	const d = parseISODate(iso);
	const total = d.getMonth() + n;
	const year = d.getFullYear() + Math.floor(total / 12);
	const month = ((total % 12) + 12) % 12;
	const day = Math.min(d.getDate(), daysInMonth(year, month));
	return toISODate(new Date(year, month, day));
}

export function addDays(iso: ISODate, n: number): ISODate {
	const d = parseISODate(iso);
	d.setDate(d.getDate() + n);
	return toISODate(d);
}

export function isSameDay(a: ISODate | Date, b: ISODate | Date): boolean {
	const x = typeof a === 'string' ? a : toISODate(a);
	const y = typeof b === 'string' ? b : toISODate(b);
	return x === y;
}

/** Whole days from a to b (negative if b is before a). */
export function diffDays(a: ISODate, b: ISODate): number {
	const ms = parseISODate(b).getTime() - parseISODate(a).getTime();
	return Math.round(ms / 86_400_000);
}

/** "2026-10" for a date, used by `/summary?month=`. */
export function monthKey(iso: ISODate): string {
	return iso.slice(0, 7);
}

/** 0 = Sunday … 6 = Saturday. */
export function weekday(iso: ISODate): number {
	return parseISODate(iso).getDay();
}

const longDate = new Intl.DateTimeFormat('en-US', { month: 'short', day: 'numeric' });
const longDateYear = new Intl.DateTimeFormat('en-US', {
	month: 'short',
	day: 'numeric',
	year: 'numeric'
});
const weekdayDate = new Intl.DateTimeFormat('en-US', {
	weekday: 'short',
	month: 'short',
	day: 'numeric'
});
const monthYear = new Intl.DateTimeFormat('en-US', { month: 'long', year: 'numeric' });
const monthShort = new Intl.DateTimeFormat('en-US', { month: 'short' });
const timeFmt = new Intl.DateTimeFormat('en-US', {
	month: 'short',
	day: 'numeric',
	hour: 'numeric',
	minute: '2-digit'
});

/** "Oct 23" (adds the year when it differs from the current one). */
export function formatDate(iso: ISODate): string {
	const d = parseISODate(iso);
	return d.getFullYear() === new Date().getFullYear() ? longDate.format(d) : longDateYear.format(d);
}

/** "Thu, Oct 23". */
export function formatWeekdayDate(iso: ISODate): string {
	return weekdayDate.format(parseISODate(iso));
}

/** "October 2026" from YYYY-MM or YYYY-MM-DD. */
export function formatMonth(isoOrMonth: string): string {
	return monthYear.format(parseISODate(`${isoOrMonth.slice(0, 7)}-01`));
}

export function formatMonthShort(iso: ISODate): string {
	return monthShort.format(parseISODate(iso));
}

/** Timestamp (ISO 8601) → "Oct 23, 4:05 PM". */
export function formatTimestamp(ts: string): string {
	const d = new Date(ts);
	return Number.isNaN(d.getTime()) ? '' : timeFmt.format(d);
}

/** Friendly relative label: "Today", "Tomorrow", "In 3 days", "2 days ago". */
export function relativeDay(iso: ISODate, from: ISODate = today()): string {
	const n = diffDays(from, iso);
	if (n === 0) return 'Today';
	if (n === 1) return 'Tomorrow';
	if (n === -1) return 'Yesterday';
	if (n > 1) return `In ${n} days`;
	return `${-n} days ago`;
}

/** Weekday headers starting on Sunday (0) or Monday (1). */
export function weekdayHeaders(weekStart: 0 | 1): string[] {
	const sun = ['S', 'M', 'T', 'W', 'T', 'F', 'S'];
	return weekStart === 1 ? [...sun.slice(1), sun[0]] : sun;
}

/** Column (0..6) of a date in a grid whose first column is `weekStart`. */
export function gridColumn(iso: ISODate, weekStart: 0 | 1): number {
	return (weekday(iso) - weekStart + 7) % 7;
}

/** "2h", "3d", "just now" style relative timestamp for activity rows. */
export function relativeTime(ts: string, now: Date = new Date()): string {
	const d = new Date(ts);
	if (Number.isNaN(d.getTime())) return '';
	const sec = Math.round((now.getTime() - d.getTime()) / 1000);
	if (sec < 45) return 'just now';
	const min = Math.round(sec / 60);
	if (min < 60) return `${min}m`;
	const hr = Math.round(min / 60);
	if (hr < 24) return `${hr}h`;
	const day = Math.round(hr / 24);
	if (day < 7) return `${day}d`;
	return formatDate(toISODate(d));
}

/** Local day (YYYY-MM-DD) of an ISO timestamp. */
export function dayOf(ts: string): ISODate {
	return toISODate(new Date(ts));
}

/** "Today", "Yesterday", or "Thu, Oct 23" for day group headers. */
export function dayHeading(iso: ISODate, from: ISODate = today()): string {
	const n = diffDays(from, iso);
	if (n === 0) return 'Today';
	if (n === -1) return 'Yesterday';
	if (n === 1) return 'Tomorrow';
	return formatWeekdayDate(iso);
}
