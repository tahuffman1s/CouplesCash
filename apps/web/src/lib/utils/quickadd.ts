/**
 * v6 one-sentence quick add (PLAN-V6 §5): "netflix 15.49 on the 7th" → a subscription, monthly,
 * day 7. Pure and unit-tested; the preview card in `QuickAddSheet.svelte` renders the draft and lets
 * each chip be corrected, the server action re-validates the corrected fields.
 *
 * Grammar (order does not matter, case does not matter):
 *   amount     `15.49`, `$1,850`, `3.2k`; `~60` / `about 60` / `around 60` = the amount varies
 *   frequency  weekly · every week · biweekly · every other week · every 2 weeks · fortnightly ·
 *              twice a month · monthly · every month · quarterly · every 3 months · every 6 months ·
 *              twice a year · yearly · annually · every year · once · one-off · one time ·
 *              every <weekday> (weekly) · every other <weekday> (biweekly)
 *   dates      today · tomorrow · <weekday> (next one, today counts) · next <weekday> (never today) ·
 *              the 7th / on the 7th / 7th / the 7 · Nov 3 / 3 Nov / November 3rd · 11/3 · 11/3/2026 ·
 *              2026-11-03 (explicit dates more than a week in the past mean next year)
 *   kind       paycheck, salary, wages, payday, income, refund, deposit, bonus, "… pay" → income;
 *              sub, subscription, streaming names (netflix, spotify, …) → subscription; else bill
 *   autopay    `autopay`, `auto-pay`, `auto pay`
 *   the rest   is the name (filler words at the edges dropped); it is also the first bank name.
 * Defaults: a day of the month without a frequency → monthly; a weekday or date without a frequency
 * → a one-off; no date → monthly from today. The category is guessed from a keyword table.
 * "Twice a month" and "every 6 months" are not API frequencies: they are saved as two monthly /
 * two yearly items (see `toRecurringBodies`).
 */
import type { Frequency, Kind } from '#lib/types.ts';

export type Cadence = Frequency | 'semimonthly' | 'semiannual';
export type QuickAddDraft = {
	text: string;
	name: string;
	kind: Kind;
	amountCents: number | null;
	amountVaries: boolean;
	cadence: Cadence;
	anchorDate: string;
	autoPay: boolean;
	/** guessed default category name ("Subscriptions"); resolve against the household's list */
	category: string | null;
	/** what the bank will probably call it: the name, so the matcher has something to go on */
	bankName: string;
	/** which parts came from the sentence (the rest are defaults) */
	explicit: { kind: boolean; amount: boolean; cadence: boolean; date: boolean };
};

export const CADENCES: readonly { value: Cadence; label: string }[] = [
	{ value: 'once', label: 'One time' },
	{ value: 'weekly', label: 'Every week' },
	{ value: 'biweekly', label: 'Every 2 weeks' },
	{ value: 'semimonthly', label: 'Twice a month' },
	{ value: 'monthly', label: 'Every month' },
	{ value: 'quarterly', label: 'Every 3 months' },
	{ value: 'semiannual', label: 'Every 6 months' },
	{ value: 'yearly', label: 'Every year' }
];
export function cadenceLabel(c: Cadence): string {
	return CADENCES.find((x) => x.value === c)?.label ?? 'Every month';
}
export function isCadence(v: string): v is Cadence {
	return CADENCES.some((c) => c.value === v);
}

// ---- dates (local, YYYY-MM-DD) -------------------------------------------------------------

const pad = (n: number) => (n < 10 ? `0${n}` : `${n}`);
const iso = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
const parse = (s: string) => {
	const [y, m, d] = s.split('-').map(Number);
	return new Date(y, m - 1, d);
};
const addDays = (s: string, n: number) => {
	const d = parse(s);
	d.setDate(d.getDate() + n);
	return iso(d);
};
const daysIn = (y: number, m: number) => new Date(y, m + 1, 0).getDate();
function addMonths(s: string, n: number): string {
	const d = parse(s);
	const total = d.getMonth() + n;
	const y = d.getFullYear() + Math.floor(total / 12);
	const m = ((total % 12) + 12) % 12;
	return iso(new Date(y, m, Math.min(d.getDate(), daysIn(y, m))));
}
/** The next date (today counts) whose day of month is `day`, clamped to short months. */
function nextDayOfMonth(today: string, day: number): string {
	const t = parse(today);
	for (let k = 0; k < 2; k++) {
		const y = t.getFullYear() + Math.floor((t.getMonth() + k) / 12);
		const m = (t.getMonth() + k) % 12;
		const candidate = iso(new Date(y, m, Math.min(day, daysIn(y, m))));
		if (candidate >= today) return candidate;
	}
	return today;
}
function nextWeekday(today: string, wd: number, strictlyAfter: boolean): string {
	const cur = parse(today).getDay();
	let diff = (wd - cur + 7) % 7;
	if (diff === 0 && strictlyAfter) diff = 7;
	return addDays(today, diff);
}
/** An explicit month/day: this year, or next year when it is more than a week in the past. */
function upcoming(today: string, month: number, day: number, year?: number): string | null {
	const t = parse(today);
	const y0 = year ?? t.getFullYear();
	if (month < 0 || month > 11 || day < 1 || day > daysIn(y0, month)) return null;
	const candidate = iso(new Date(y0, month, day));
	if (year !== undefined || candidate >= addDays(today, -7)) return candidate;
	return iso(new Date(y0 + 1, month, Math.min(day, daysIn(y0 + 1, month))));
}

// ---- vocabulary ----------------------------------------------------------------------------

const WEEKDAYS: Record<string, number> = {
	// "sun", "sat" and "wed" are left out on purpose: they are too often part of a name
	sunday: 0, mon: 1, monday: 1, tue: 2, tues: 2, tuesday: 2, wednesday: 3, thu: 4, thur: 4, thurs: 4, thursday: 4, fri: 5, friday: 5, saturday: 6
}; // prettier-ignore
const MONTHS: Record<string, number> = {
	jan: 0, january: 0, feb: 1, february: 1, mar: 2, march: 2, apr: 3, april: 3, may: 4, jun: 5,
	june: 5, jul: 6, july: 6, aug: 7, august: 7, sep: 8, sept: 8, september: 8, oct: 9,
	october: 9, nov: 10, november: 10, dec: 11, december: 11
}; // prettier-ignore
const NUMBER_WORDS: Record<string, number> = {
	one: 1,
	two: 2,
	three: 3,
	four: 4,
	six: 6,
	twelve: 12
};
const VARIES_WORDS = new Set([
	'~',
	'≈',
	'about',
	'around',
	'approx',
	'approximately',
	'roughly',
	'ca'
]);
const FILLER = new Set(['on', 'the', 'for', 'at', 'due', 'every', 'each', 'per', 'is', 'a', 'an', 'of', 'and', 'from', '-', '–', '$', 'my', 'our', 'starting']); // prettier-ignore
const INCOME_WORDS = new Set(['paycheck', 'paycheque', 'payday', 'salary', 'wage', 'wages', 'income', 'refund', 'deposit', 'bonus', 'payroll', 'reimbursement']); // prettier-ignore
const STREAMING = ['netflix', 'spotify', 'hulu', 'disney+', 'disney', 'hbo', 'youtube', 'apple music', 'apple tv', 'icloud', 'prime', 'audible', 'peacock', 'paramount', 'crunchyroll', 'chatgpt', 'xbox', 'playstation', 'nintendo', 'dropbox', 'adobe', 'patreon', 'google one', 'siriusxm', 'sirius', 'pandora', 'tidal', 'duolingo', 'substack', 'kindle', 'twitch']; // prettier-ignore

/** Keyword → default category name (first hit wins; checked against whole words / phrases). */
const CATEGORY_KEYWORDS: [string, string[]][] = [
	['Insurance', ['insurance', 'geico', 'allstate', 'progressive', 'state farm', 'premium', 'lemonade']],
	['Debt', ['loan', 'credit card', 'card payment', 'car payment', 'debt', 'affirm', 'klarna', 'student loan']],
	['Housing', ['rent', 'mortgage', 'hoa', 'landlord', 'lease', 'property tax', 'housing']],
	['Utilities', ['electric', 'electricity', 'power', 'water', 'sewer', 'trash', 'gas bill', 'internet', 'wifi', 'broadband', 'phone', 'cell', 'mobile', 'comcast', 'xfinity', 'verizon', 'at&t', 'att', 't-mobile', 'tmobile', 'utility', 'utilities', 'heating']],
	['Health', ['gym', 'doctor', 'dentist', 'dental', 'pharmacy', 'medicine', 'therapy', 'health', 'medical', 'vision', 'fitness', 'yoga', 'pilates']],
	['Kids & Pets', ['daycare', 'childcare', 'school', 'tuition', 'kids', 'kid', 'baby', 'vet', 'pet', 'dog', 'cat', 'nanny', 'babysitter', 'allowance']],
	['Groceries', ['grocery', 'groceries', 'kroger', 'costco', 'aldi', 'safeway', 'walmart', 'trader joe', 'whole foods', 'publix', 'supermarket']],
	['Transportation', ['car', 'oil change', 'oil', 'tire', 'tires', 'tyres', 'fuel', 'gas', 'uber', 'lyft', 'parking', 'toll', 'tolls', 'transit', 'metro', 'bus', 'train', 'registration', 'dmv']],
	['Fun', ['dinner', 'restaurant', 'movies', 'movie', 'concert', 'tickets', 'vacation', 'trip', 'coffee', 'bar', 'party', 'gift', 'games', 'birthday']]
]; // prettier-ignore

function hasPhrase(words: string[], phrase: string): boolean {
	const p = phrase.split(' ');
	for (let i = 0; i + p.length <= words.length; i++)
		if (p.every((w, j) => words[i + j] === w)) return true;
	return false;
}

/** Default category name for a name and kind (null = none). */
export function guessCategory(name: string, kind: Kind): string | null {
	if (kind === 'income') return 'Income';
	const words = name.toLowerCase().split(/\s+/).filter(Boolean);
	if (STREAMING.some((s) => hasPhrase(words, s))) return 'Subscriptions';
	for (const [category, keys] of CATEGORY_KEYWORDS)
		if (keys.some((k) => hasPhrase(words, k))) return category;
	return kind === 'subscription' ? 'Subscriptions' : null;
}

/** Kind implied by the name's words (paycheck → income, netflix → subscription), else null. */
export function guessKind(name: string): Kind | null {
	const words = name.toLowerCase().split(/\s+/).filter(Boolean);
	if (words.some((w) => INCOME_WORDS.has(w))) return 'income';
	// "pay" is income only as the last word ("Alex pay", "take home pay"), never "pay rent"
	if (words.length > 0 && words[words.length - 1] === 'pay') return 'income';
	if (STREAMING.some((s) => hasPhrase(words, s))) return 'subscription';
	return null;
}

const AMOUNT_RE = /^(~|≈)?\$?((?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d{1,2})?|\.\d{1,2})(k)?$/i;
/** "1,850.00" / "$15.49" / "3.2k" → cents (+ whether it was marked as approximate with "~"). */
export function parseAmountToken(token: string): { cents: number; varies: boolean } | null {
	const m = AMOUNT_RE.exec(token.trim());
	if (!m) return null;
	const value = Number(m[2].replace(/,/g, ''));
	if (!Number.isFinite(value)) return null;
	const cents = Math.round(value * (m[3] ? 1000 : 1) * 100);
	return { cents, varies: !!m[1] };
}

const ORDINAL_RE = /^(\d{1,2})(st|nd|rd|th)$/;
const SLASH_RE = /^(\d{1,2})\/(\d{1,2})(?:\/(\d{2}|\d{4}))?$/;
const ISO_RE = /^(\d{4})-(\d{2})-(\d{2})$/;

/** Splits a sentence into tokens, keeping "$1,850.00", "~60" and "11/3" together. */
function tokenize(text: string): string[] {
	return text
		.replace(/[\u00a0\t\n]/g, ' ')
		.replace(/(^|\s)~\s+(?=\$?\d)/g, '$1~') // "~ 60" → "~60"
		.replace(/(^|\s)\$\s+(?=\d)/g, '$1$') // "$ 60" → "$60"
		.split(' ')
		.map((t) => t.replace(/^[(]+|[),;:!?]+$/g, '').replace(/(\D)\.$/, '$1'))
		.filter(Boolean);
}

function capitalise(name: string): string {
	if (!name) return name;
	return name === name.toLowerCase() ? name.charAt(0).toUpperCase() + name.slice(1) : name;
}

/** Parses one quick-add sentence. `today` is the household's local date (YYYY-MM-DD). */
export function parseQuickAdd(text: string, today: string): QuickAddDraft {
	const tokens = tokenize(text);
	const lower = tokens.map((t) => t.toLowerCase());
	const used = tokens.map(() => false);
	const take = (i: number, n = 1) => {
		for (let k = i; k < i + n; k++) used[k] = true;
	};
	const at = (i: number) => (i >= 0 && i < lower.length && !used[i] ? lower[i] : null);

	let cadence: Cadence | null = null;
	let date: string | null = null;
	let dayOfMonth: number | null = null;
	let weekdayHint: { wd: number; strict: boolean } | null = null;
	let autoPay = false;
	let amount: { cents: number; varies: boolean } | null = null;
	let variesWord = false;
	let kindMarker: Kind | null = null;

	const numberAt = (i: number): number | null => {
		const w = at(i);
		if (w === null) return null;
		if (/^\d+$/.test(w)) return Number(w);
		return NUMBER_WORDS[w] ?? null;
	};

	// 1. autopay + kind markers
	for (let i = 0; i < lower.length; i++) {
		const w = at(i);
		if (w === 'autopay' || w === 'auto-pay' || w === 'autopaid') {
			autoPay = true;
			take(i);
		} else if (w === 'auto' && at(i + 1) === 'pay') {
			autoPay = true;
			take(i, 2);
		}
	}

	// 2. frequency phrases (longest first)
	for (let i = 0; i < lower.length; i++) {
		const w = at(i);
		if (w === null) continue;
		const next = at(i + 1);
		const next2 = at(i + 2);
		if (w === 'every' || w === 'each') {
			if (next === 'other' && next2 !== null) {
				if (/^weeks?$/.test(next2)) {
					cadence = 'biweekly';
					take(i, 3);
				} else if (next2 in WEEKDAYS) {
					cadence = 'biweekly';
					weekdayHint = { wd: WEEKDAYS[next2], strict: false };
					take(i, 3);
				} else if (/^months?$/.test(next2)) {
					cadence = 'semiannual'; // rare; keeps the meaning roughly (every 2 months ≠ API)
					take(i, 3);
				}
				continue;
			}
			const n = numberAt(i + 1);
			if (n !== null && next2 !== null) {
				if (/^weeks?$/.test(next2) && n === 2) {
					cadence = 'biweekly';
					take(i, 3);
				} else if (/^weeks?$/.test(next2) && n === 1) {
					cadence = 'weekly';
					take(i, 3);
				} else if (/^months?$/.test(next2) && (n === 1 || n === 3 || n === 6 || n === 12)) {
					cadence = n === 1 ? 'monthly' : n === 3 ? 'quarterly' : n === 6 ? 'semiannual' : 'yearly';
					take(i, 3);
				} else if (/^years?$/.test(next2) && n === 1) {
					cadence = 'yearly';
					take(i, 3);
				}
				continue;
			}
			const unit: Record<string, Cadence> = {
				week: 'weekly',
				fortnight: 'biweekly',
				month: 'monthly',
				quarter: 'quarterly',
				year: 'yearly'
			};
			if (next !== null && next in unit) {
				cadence = unit[next];
				take(i, 2);
			} else if (next !== null && next in WEEKDAYS) {
				cadence = 'weekly';
				weekdayHint = { wd: WEEKDAYS[next], strict: false };
				take(i, 2);
			}
			continue;
		}
		if (w === 'twice' && (next === 'a' || next === 'per') && next2 !== null) {
			if (next2 === 'month' || next2 === 'year') {
				cadence = next2 === 'month' ? 'semimonthly' : 'semiannual';
				take(i, 3);
			}
			continue;
		}
		if ((w === 'a' || w === 'per' || w === '/') && next !== null && cadence === null) {
			const map: Record<string, Cadence> = { week: 'weekly', month: 'monthly', year: 'yearly' };
			if (next in map && i > 0) {
				cadence = map[next];
				take(i, 2);
			}
			continue;
		}
		if (w === 'one' && (next === 'time' || next === 'off')) {
			cadence = 'once';
			take(i, 2);
			continue;
		}
		if (w === 'just' && next === 'once') {
			cadence = 'once';
			take(i, 2);
			continue;
		}
		const single: Record<string, Cadence> = {
			weekly: 'weekly',
			biweekly: 'biweekly',
			'bi-weekly': 'biweekly',
			fortnightly: 'biweekly',
			semimonthly: 'semimonthly',
			'semi-monthly': 'semimonthly',
			bimonthly: 'semimonthly',
			monthly: 'monthly',
			'/mo': 'monthly',
			'/month': 'monthly',
			'/wk': 'weekly',
			'/yr': 'yearly',
			quarterly: 'quarterly',
			semiannually: 'semiannual',
			'semi-annually': 'semiannual',
			semiannual: 'semiannual',
			biannually: 'semiannual',
			yearly: 'yearly',
			annually: 'yearly',
			annual: 'yearly',
			once: 'once',
			'one-off': 'once',
			oneoff: 'once',
			'one-time': 'once',
			onetime: 'once'
		};
		if (w in single) {
			cadence = single[w];
			take(i);
		}
	}

	// 3. dates
	for (let i = 0; i < lower.length; i++) {
		const w = at(i);
		if (w === null || date !== null) continue;
		if (w === 'today' || w === 'tonight') {
			date = today;
			take(i);
		} else if (w === 'tomorrow' || w === 'tmrw') {
			date = addDays(today, 1);
			take(i);
		} else if ((w === 'next' || w === 'this') && at(i + 1) !== null && at(i + 1)! in WEEKDAYS) {
			const wd = WEEKDAYS[at(i + 1)!];
			date = nextWeekday(today, wd, w === 'next');
			take(i, 2);
		} else if (w in WEEKDAYS) {
			if (weekdayHint === null) weekdayHint = { wd: WEEKDAYS[w], strict: false };
			date = nextWeekday(today, WEEKDAYS[w], false);
			take(i);
		} else if (ORDINAL_RE.test(w) && Number(ORDINAL_RE.exec(w)![1]) <= 31) {
			const n = Number(ORDINAL_RE.exec(w)![1]);
			// "Nov 3rd" / "3rd of Nov" are explicit dates
			const before = at(i - 1);
			const after = at(i + 1) === 'of' ? at(i + 2) : at(i + 1);
			if (before !== null && before in MONTHS) {
				date = upcoming(today, MONTHS[before], n);
				take(i - 1, 2);
			} else if (after !== null && after in MONTHS) {
				date = upcoming(today, MONTHS[after], n);
				take(i, at(i + 1) === 'of' ? 3 : 2);
			} else if (n >= 1) {
				dayOfMonth = n;
				date = nextDayOfMonth(today, n);
				take(i);
			}
		} else if (w === 'the' && /^\d{1,2}$/.test(at(i + 1) ?? '') && Number(at(i + 1)) <= 31) {
			const n = Number(at(i + 1));
			if (n >= 1) {
				dayOfMonth = n;
				date = nextDayOfMonth(today, n);
				take(i, 2);
			}
		} else if (w in MONTHS && /^\d{1,2}(st|nd|rd|th)?$/.test(at(i + 1) ?? '')) {
			const n = parseInt(at(i + 1)!, 10);
			date = upcoming(today, MONTHS[w], n);
			if (date) take(i, 2);
		} else if (
			/^\d{1,2}$/.test(w) &&
			at(i + 1) !== null &&
			at(i + 1)! in MONTHS &&
			Number(w) <= 31 &&
			// "25 dec 3" is "$25 on Dec 3", not "Dec 25" + "$3"
			!/^\d{1,2}(st|nd|rd|th)?$/.test(at(i + 2) ?? '')
		) {
			date = upcoming(today, MONTHS[at(i + 1)!], Number(w));
			if (date) take(i, 2);
		} else if (SLASH_RE.test(w)) {
			const m = SLASH_RE.exec(w)!;
			const year = m[3] ? (m[3].length === 2 ? 2000 + Number(m[3]) : Number(m[3])) : undefined;
			date = upcoming(today, Number(m[1]) - 1, Number(m[2]), year);
			if (date) take(i);
		} else if (ISO_RE.test(w)) {
			const m = ISO_RE.exec(w)!;
			date = upcoming(today, Number(m[2]) - 1, Number(m[3]), Number(m[1]));
			if (date) take(i);
		}
	}
	// "on" / "due" right before a consumed date is part of it
	for (let i = 0; i < lower.length - 1; i++)
		if (!used[i] && (lower[i] === 'on' || lower[i] === 'due') && used[i + 1]) take(i);

	// 4. amount (first number left; "about" / "~" before it marks it as varying)
	for (let i = 0; i < lower.length; i++) {
		const w = at(i);
		if (w === null) continue;
		if (VARIES_WORDS.has(w) && at(i + 1) !== null && parseAmountToken(at(i + 1)!)) {
			variesWord = true;
			take(i);
			continue;
		}
		const a = parseAmountToken(w);
		if (a && !amount) {
			amount = a;
			take(i);
		}
	}

	// 5. explicit kind markers ("spotify sub 11.99", "income 500") – only when a name is left
	const leftover = () => tokens.filter((_, i) => !used[i] && !FILLER.has(lower[i]));
	for (let i = 0; i < lower.length; i++) {
		const w = at(i);
		if (w === null) continue;
		const marker: Kind | null =
			w === 'sub' || w === 'subscription'
				? 'subscription'
				: w === 'bill'
					? 'bill'
					: w === 'income'
						? 'income'
						: null;
		if (marker && leftover().length > 1) {
			kindMarker = marker;
			// "water bill" keeps "bill" in the name; a trailing "sub"/"income" is just a marker
			if (marker !== 'bill') take(i);
		} else if (marker) kindMarker = marker;
	}

	// 6. the name: what is left, filler trimmed at both ends
	const rest = tokens.filter((_, i) => !used[i]);
	while (rest.length && FILLER.has(rest[0].toLowerCase())) rest.shift();
	while (rest.length && FILLER.has(rest[rest.length - 1].toLowerCase())) rest.pop();
	const name = capitalise(rest.join(' ').slice(0, 80));

	const kind: Kind = kindMarker ?? guessKind(name) ?? 'bill';

	// 7. defaults
	const explicitCadence = cadence !== null;
	if (cadence === null) {
		if (dayOfMonth !== null) cadence = 'monthly';
		else if (date !== null) cadence = 'once';
		else cadence = 'monthly';
	}
	let anchorDate = date ?? today;
	if ((cadence === 'weekly' || cadence === 'biweekly') && weekdayHint && date === null)
		anchorDate = nextWeekday(today, weekdayHint.wd, weekdayHint.strict);

	return {
		text,
		name,
		kind,
		amountCents: amount ? amount.cents : null,
		amountVaries: !!amount && (amount.varies || variesWord),
		cadence,
		anchorDate,
		autoPay: autoPay && kind !== 'income',
		category: guessCategory(name, kind),
		bankName: name,
		explicit: {
			kind: kindMarker !== null || guessKind(name) !== null,
			amount: amount !== null,
			cadence: explicitCadence,
			date: date !== null || weekdayHint !== null
		}
	};
}

/** What is missing before the draft can be saved (empty = ready). */
export function draftProblems(d: Pick<QuickAddDraft, 'name' | 'amountCents'>): string[] {
	const out: string[] = [];
	if (!d.name.trim()) out.push('Add a name, like "Netflix"');
	if (d.amountCents === null || d.amountCents <= 0) out.push('Add an amount, like 15.49');
	return out;
}

function ordinal(n: number): string {
	const v = n % 100;
	if (v >= 11 && v <= 13) return `${n}th`;
	return `${n}${['th', 'st', 'nd', 'rd'][n % 10] ?? 'th'}`;
}
const MONTH_SHORT = [
	'Jan',
	'Feb',
	'Mar',
	'Apr',
	'May',
	'Jun',
	'Jul',
	'Aug',
	'Sep',
	'Oct',
	'Nov',
	'Dec'
];
const WEEKDAY_SHORT = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

/** "Fri, Oct 30" (adds the year when it is not `today`'s year). */
export function shortDate(date: string, today: string): string {
	const d = parse(date);
	const base = `${WEEKDAY_SHORT[d.getDay()]}, ${MONTH_SHORT[d.getMonth()]} ${d.getDate()}`;
	return d.getFullYear() === parse(today).getFullYear() ? base : `${base}, ${d.getFullYear()}`;
}

/** The second date of a split cadence: anchor + 15 days (twice a month) or + 6 months. */
export function secondAnchor(d: Pick<QuickAddDraft, 'cadence' | 'anchorDate'>): string | null {
	if (d.cadence === 'semiannual') return addMonths(d.anchorDate, 6);
	if (d.cadence === 'semimonthly') {
		const a = parse(d.anchorDate);
		const day = a.getDate();
		const other = day > 15 ? day - 15 : day + 15;
		// the other payday of the same month when it is later, else next month's
		const sameMonth = iso(
			new Date(
				a.getFullYear(),
				a.getMonth(),
				Math.min(other, daysIn(a.getFullYear(), a.getMonth()))
			)
		);
		return sameMonth > d.anchorDate ? sameMonth : addMonths(sameMonth, 1);
	}
	return null;
}

/** "every month · next Sat, Nov 7" / "one time · Fri, Oct 30" for the preview and the toast. */
export function scheduleText(
	d: Pick<QuickAddDraft, 'cadence' | 'anchorDate'>,
	today: string
): string {
	const when = shortDate(d.anchorDate, today);
	if (d.cadence === 'once') return `one time · ${when}`;
	const second = secondAnchor(d);
	if (second)
		return `${cadenceLabel(d.cadence).toLowerCase()} · ${when} and ${shortDate(second, today)}`;
	return `${cadenceLabel(d.cadence).toLowerCase()} · next ${when}`;
}

export type QuickAddBody = {
	name: string;
	kind: Kind;
	amountCents: number;
	categoryId: string | null;
	accountId: string | null;
	frequency: Frequency;
	anchorDate: string;
	endDate: null;
	autoPay: boolean;
	notes: string | null;
	url: null;
	bankNames: string[];
	amountVaries: boolean;
};

/**
 * `POST /recurring` bodies for a finished draft. A split cadence becomes two items ("Paycheck · 1st"
 * and "Paycheck · 16th" monthly; "Car insurance · Nov" and "Car insurance · May" yearly).
 */
export function toRecurringBodies(
	d: Pick<
		QuickAddDraft,
		| 'name'
		| 'kind'
		| 'amountCents'
		| 'amountVaries'
		| 'cadence'
		| 'anchorDate'
		| 'autoPay'
		| 'bankName'
	>,
	opts: { categoryId: string | null; accountId: string | null }
): QuickAddBody[] {
	if (d.amountCents === null || d.amountCents <= 0 || !d.name.trim()) return [];
	const base = {
		kind: d.kind,
		amountCents: d.amountCents,
		categoryId: opts.categoryId,
		accountId: opts.accountId,
		endDate: null,
		autoPay: d.kind !== 'income' && d.autoPay,
		url: null,
		bankNames: d.bankName.trim().length >= 2 ? [d.bankName.trim().slice(0, 80)] : [],
		amountVaries: d.amountVaries
	} as const;
	const second = secondAnchor(d);
	if (!second) {
		const frequency = d.cadence as Frequency;
		return [{ ...base, name: d.name.trim(), frequency, anchorDate: d.anchorDate, notes: null }];
	}
	const yearly = d.cadence === 'semiannual';
	const label = (date: string) =>
		yearly ? MONTH_SHORT[parse(date).getMonth()] : ordinal(parse(date).getDate());
	const note = yearly
		? 'Every 6 months (saved as two yearly items)'
		: 'Twice a month (saved as two monthly items)';
	return [d.anchorDate, second].map((anchorDate) => ({
		...base,
		name: `${d.name.trim()} · ${label(anchorDate)}`.slice(0, 80),
		frequency: yearly ? ('yearly' as const) : ('monthly' as const),
		anchorDate,
		notes: note
	}));
}
