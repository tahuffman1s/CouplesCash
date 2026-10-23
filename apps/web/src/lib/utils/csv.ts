/**
 * Client-side CSV import helpers: header detection, column guessing, amount/date parsing and the
 * final conversion into `POST /imports/csv` rows. Pure functions, no I/O (papaparse does the
 * tokenising on the page).
 */
import type { CsvImportRow } from '#lib/types.ts';
import { isValidISODate } from './dates';

export type Row = string[];
export type DateFormat = 'iso' | 'mdy' | 'dmy' | 'text';
export type Mapping = {
	date: number | null;
	description: number | null;
	/** single signed amount column; null when debit/credit are used */
	amount: number | null;
	debit: number | null;
	credit: number | null;
	payee: number | null;
	memo: number | null;
};
export type BuildOptions = {
	mapping: Mapping;
	dateFormat: DateFormat;
	/** single-amount mode: true = money out is negative in the file (the common convention) */
	outflowsNegative: boolean;
	hasHeader: boolean;
};

export const MAX_ROWS = 5000;
export const MAX_FILE_BYTES = 2 * 1024 * 1024;
export const DATE_FORMAT_LABEL: Record<DateFormat, string> = {
	iso: 'YYYY-MM-DD',
	mdy: 'MM/DD/YYYY',
	dmy: 'DD/MM/YYYY',
	text: 'Oct 3, 2026'
};

const MONTHS = ['jan', 'feb', 'mar', 'apr', 'may', 'jun', 'jul', 'aug', 'sep', 'oct', 'nov', 'dec'];
const pad = (n: number) => (n < 10 ? `0${n}` : String(n));

/**
 * "1,234.56" → 123456 · "(12.34)" → -1234 · "$-5.00" → -500 · "−5.00" (U+2212) → -500 ·
 * "1.234,56" → 123456 · "12.5" → 1250. Returns null for blanks and non-numbers.
 */
export function parseAmount(input: string | null | undefined): number | null {
	if (input == null) return null;
	let s = input.trim();
	if (!s) return null;
	let negative = false;
	if (/^\(.*\)$/.test(s)) {
		negative = true;
		s = s.slice(1, -1);
	}
	// unicode minus / en dash / trailing minus / "CR" and "DR" suffixes
	s = s.replace(/[−–]/g, '-');
	if (/\bDR\b/i.test(s)) negative = true;
	s = s.replace(/\b(CR|DR)\b/gi, '');
	if (/-\s*$/.test(s)) {
		negative = true;
		s = s.replace(/-\s*$/, '');
	}
	// currency symbols, codes, spaces and leading plus
	s = s.replace(/[^\d.,+-]/g, '');
	if (s.includes('-')) {
		negative = negative || s.indexOf('-') === s.search(/[\d.,]/) - 1 || s.startsWith('-');
		s = s.replace(/-/g, '');
	}
	s = s.replace(/\+/g, '');
	if (!s) return null;

	// decimal separator: whichever of . or , comes last; a lone comma followed by 2 digits is decimal
	const lastDot = s.lastIndexOf('.');
	const lastComma = s.lastIndexOf(',');
	let whole: string;
	let frac = '';
	if (lastDot === -1 && lastComma === -1) {
		whole = s;
	} else if (lastDot > lastComma) {
		whole = s.slice(0, lastDot).replace(/[.,]/g, '');
		frac = s.slice(lastDot + 1);
	} else if (lastDot === -1 && /,\d{3}$/.test(s) && !/,\d{3},/.test(s) && s.indexOf(',') > 0) {
		// "1,234" – a single comma with three digits after it is a thousands separator
		whole = s.replace(/,/g, '');
	} else {
		whole = s.slice(0, lastComma).replace(/[.,]/g, '');
		frac = s.slice(lastComma + 1);
	}
	if (!/^\d*$/.test(whole) || !/^\d*$/.test(frac)) return null;
	if (whole === '' && frac === '') return null;
	const cents =
		Number(whole || '0') * 100 + Math.round(Number(`0.${frac || '0'}`.slice(0, 6)) * 100);
	if (!Number.isFinite(cents)) return null;
	return negative ? -cents : cents;
}

/** Parses one cell in the given format into YYYY-MM-DD, or null. */
export function parseDate(input: string | null | undefined, format: DateFormat): string | null {
	if (input == null) return null;
	const s = input.trim();
	if (!s) return null;
	let y: number, m: number, d: number;
	if (format === 'iso') {
		const mm = /^(\d{4})-(\d{1,2})-(\d{1,2})(?:[T\s].*)?$/.exec(s);
		if (!mm) return null;
		[y, m, d] = [Number(mm[1]), Number(mm[2]), Number(mm[3])];
	} else if (format === 'mdy' || format === 'dmy') {
		const mm = /^(\d{1,2})[/.-](\d{1,2})[/.-](\d{2,4})(?:[T\s].*)?$/.exec(s);
		if (!mm) return null;
		const a = Number(mm[1]);
		const b = Number(mm[2]);
		y = Number(mm[3]);
		if (mm[3].length === 2) y += y < 70 ? 2000 : 1900;
		[m, d] = format === 'mdy' ? [a, b] : [b, a];
	} else {
		// "Oct 3, 2026", "October 3 2026", "3 Oct 2026", "3 October 2026"
		let mm = /^([A-Za-z]{3,})\.?\s+(\d{1,2}),?\s+(\d{4})$/.exec(s);
		let monthName: string;
		if (mm) {
			monthName = mm[1];
			d = Number(mm[2]);
			y = Number(mm[3]);
		} else {
			mm = /^(\d{1,2})\s+([A-Za-z]{3,})\.?,?\s+(\d{4})$/.exec(s);
			if (!mm) return null;
			d = Number(mm[1]);
			monthName = mm[2];
			y = Number(mm[3]);
		}
		const idx = MONTHS.indexOf(monthName.slice(0, 3).toLowerCase());
		if (idx < 0) return null;
		m = idx + 1;
	}
	const iso = `${y}-${pad(m)}-${pad(d)}`;
	return isValidISODate(iso) ? iso : null;
}

/** Picks the format that parses every non-empty sample; slashes default to MM/DD unless a day > 12 says otherwise. */
export function detectDateFormat(samples: string[]): DateFormat | null {
	const vals = samples.map((s) => (s ?? '').trim()).filter(Boolean);
	if (!vals.length) return null;
	if (vals.every((v) => parseDate(v, 'iso'))) return 'iso';
	if (vals.every((v) => parseDate(v, 'text'))) return 'text';
	const slash = vals.map((v) => /^(\d{1,2})[/.-](\d{1,2})[/.-](\d{2,4})/.exec(v));
	if (slash.every(Boolean)) {
		const firstOver12 = slash.some((m) => Number(m![1]) > 12);
		const secondOver12 = slash.some((m) => Number(m![2]) > 12);
		if (firstOver12 && !secondOver12) return 'dmy';
		if (secondOver12 && !firstOver12) return 'mdy';
		if (vals.every((v) => parseDate(v, 'mdy'))) return 'mdy';
		if (vals.every((v) => parseDate(v, 'dmy'))) return 'dmy';
	}
	return null;
}

function looksLikeDate(cell: string): boolean {
	return detectDateFormat([cell]) !== null;
}

/**
 * True when the first row reads like column names: no cell is a date, at least one cell is a
 * word, and a later row does contain a date.
 */
export function detectHeader(rows: Row[]): boolean {
	if (rows.length < 2) return false;
	const first = rows[0];
	if (first.some((c) => looksLikeDate(c))) return false;
	if (!first.some((c) => /[A-Za-z]{2,}/.test(c))) return false;
	return rows.slice(1, 6).some((r) => r.some((c) => looksLikeDate(c)));
}

const HEADER_PATTERNS: [keyof Mapping, RegExp][] = [
	['date', /\b(date|posted|post date|transaction date|booking)\b/i],
	['debit', /\b(debit|withdrawal|withdrawals|money out|outflow|paid out|spent)\b/i],
	['credit', /\b(credit|deposit|deposits|money in|inflow|paid in|received)\b/i],
	['amount', /\b(amount|amt|value|sum|total)\b/i],
	['payee', /\b(payee|vendor|merchant|counterparty|name)\b/i],
	['memo', /\b(memo|note|notes|reference|ref|category)\b/i],
	['description', /\b(description|desc|details|narrative|transaction|particulars|text)\b/i]
];

/** Guesses the column mapping from header names first, then from the look of the sample values. */
export function guessMapping(headers: string[] | null, rows: Row[]): Mapping {
	const width = Math.max(headers?.length ?? 0, ...rows.slice(0, 20).map((r) => r.length), 0);
	const mapping: Mapping = {
		date: null,
		description: null,
		amount: null,
		debit: null,
		credit: null,
		payee: null,
		memo: null
	};
	const used = new Set<number>();
	const claim = (key: keyof Mapping, col: number) => {
		if (mapping[key] !== null || used.has(col)) return;
		mapping[key] = col;
		used.add(col);
	};

	if (headers) {
		for (const [key, re] of HEADER_PATTERNS) {
			headers.forEach((h, i) => {
				if (re.test(h.trim())) claim(key, i);
			});
		}
	}

	const sample = rows.slice(0, 20);
	const colValues = (i: number) => sample.map((r) => (r[i] ?? '').trim()).filter((v) => v !== '');
	const share = (i: number, test: (v: string) => boolean) => {
		const vals = colValues(i);
		return vals.length ? vals.filter(test).length / vals.length : 0;
	};

	// dates
	if (mapping.date === null) {
		for (let i = 0; i < width; i++) {
			if (!used.has(i) && share(i, looksLikeDate) >= 0.8) {
				claim('date', i);
				break;
			}
		}
	}
	// numeric columns → amount (or debit/credit when two half-empty numeric columns sit side by side)
	const numeric: number[] = [];
	for (let i = 0; i < width; i++) {
		if (used.has(i)) continue;
		const vals = colValues(i);
		if (!vals.length) continue;
		const ok = vals.filter((v) => parseAmount(v) !== null && !/[A-Za-z]{2,}/.test(v)).length;
		if (ok / vals.length >= 0.8) numeric.push(i);
	}
	if (mapping.amount === null && mapping.debit === null && mapping.credit === null) {
		const sparse = numeric.filter(
			(i) => colValues(i).length < sample.length && colValues(i).length > 0
		);
		if (sparse.length >= 2 && sample.length >= 2) {
			claim('debit', sparse[0]);
			claim('credit', sparse[1]);
		} else if (numeric.length) {
			// prefer the column with mixed signs
			const mixed = numeric.find(
				(i) =>
					share(i, (v) => (parseAmount(v) ?? 0) < 0) > 0 &&
					share(i, (v) => (parseAmount(v) ?? 0) > 0) > 0
			);
			claim('amount', mixed ?? numeric[0]);
		}
	}
	// description = the remaining text column with the longest average length
	if (mapping.description === null) {
		let best = -1;
		let bestLen = 0;
		for (let i = 0; i < width; i++) {
			if (used.has(i)) continue;
			const vals = colValues(i);
			if (!vals.length) continue;
			const avg = vals.reduce((s, v) => s + v.length, 0) / vals.length;
			if (share(i, (v) => /[A-Za-z]/.test(v)) >= 0.5 && avg > bestLen) {
				best = i;
				bestLen = avg;
			}
		}
		if (best >= 0) {
			if (mapping.payee !== null && mapping.description === null && best === mapping.payee) {
				// nothing – payee already took it
			} else claim('description', best);
		}
	}
	// a file with only a payee column: the payee doubles as the description
	if (mapping.description === null && mapping.payee !== null) mapping.description = mapping.payee;
	return mapping;
}

/** Signed cents for a row: single amount column (with sign convention) or credit − debit. */
export function rowAmount(row: Row, mapping: Mapping, outflowsNegative: boolean): number | null {
	if (mapping.amount !== null) {
		const v = parseAmount(row[mapping.amount]);
		if (v === null) return null;
		return outflowsNegative ? v : -v;
	}
	const debit = mapping.debit !== null ? parseAmount(row[mapping.debit]) : null;
	const credit = mapping.credit !== null ? parseAmount(row[mapping.credit]) : null;
	if (debit === null && credit === null) return null;
	return (credit !== null ? Math.abs(credit) : 0) - (debit !== null ? Math.abs(debit) : 0);
}

/**
 * Turns parsed cells into contract rows. Rows without a usable date or a non-zero amount are
 * counted in `skipped`; output is capped at MAX_ROWS.
 */
export function buildRows(
	rows: Row[],
	opts: BuildOptions
): { rows: CsvImportRow[]; skipped: number; total: number } {
	const data = opts.hasHeader ? rows.slice(1) : rows;
	const out: CsvImportRow[] = [];
	let skipped = 0;
	const { mapping } = opts;
	for (const row of data) {
		if (row.every((c) => (c ?? '').trim() === '')) continue;
		const postedOn = mapping.date !== null ? parseDate(row[mapping.date], opts.dateFormat) : null;
		const amountCents = rowAmount(row, mapping, opts.outflowsNegative);
		const description =
			(mapping.description !== null ? row[mapping.description] : '')?.trim() ||
			(mapping.payee !== null ? row[mapping.payee] : '')?.trim() ||
			'';
		if (!postedOn || amountCents === null || amountCents === 0 || !description) {
			skipped++;
			continue;
		}
		if (out.length >= MAX_ROWS) {
			skipped++;
			continue;
		}
		const r: CsvImportRow = { postedOn, amountCents, description: description.slice(0, 200) };
		const payee = mapping.payee !== null ? row[mapping.payee]?.trim() : '';
		const memo = mapping.memo !== null ? row[mapping.memo]?.trim() : '';
		if (payee && payee !== description) r.payee = payee.slice(0, 120);
		if (memo) r.memo = memo.slice(0, 500);
		out.push(r);
	}
	return { rows: out, skipped, total: out.length + skipped };
}

/** Columns' display names for the mapping selects: header text or "Column 3" with a sample. */
export function columnLabels(headers: string[] | null, rows: Row[], width: number): string[] {
	const sampleRow = rows.find((r) => r.some((c) => c && c.trim())) ?? [];
	return Array.from({ length: width }, (_, i) => {
		const h = headers?.[i]?.trim();
		const s = (sampleRow[i] ?? '').trim();
		const sample = s ? ` (e.g. ${s.length > 18 ? `${s.slice(0, 18)}…` : s})` : '';
		return h ? `${h}${sample}` : `Column ${i + 1}${sample}`;
	});
}
