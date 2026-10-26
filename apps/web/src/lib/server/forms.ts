import type { AccountType, Frequency, Kind, PayPeriod } from '#lib/types.ts';
import { ACCOUNT_TYPES, FREQUENCIES, KINDS, PAY_PERIODS } from '#lib/types.ts';
import type { ExpenseBody, GoalBody, HouseholdPatch, TodoBody } from './api';
import { parseMoney } from '#lib/utils/money.ts';
import { isValidISODate } from '#lib/utils/dates.ts';

export function str(data: FormData, key: string): string {
	return String(data.get(key) ?? '').trim();
}

export function optional(data: FormData, key: string): string | null {
	const v = str(data, key);
	return v === '' ? null : v;
}

/**
 * v6 "Bank shows it as": one hidden `bankName` input per chip, plus whatever is still typed in the
 * chip field (`bankNameDraft`, comma separated) so it also works without JavaScript.
 */
export function parseBankNames(data: FormData): Parsed<string[]> {
	const raw = [...data.getAll('bankName').map(String), ...str(data, 'bankNameDraft').split(',')]
		.map((s) => s.trim().replace(/\s+/g, ' '))
		.filter(Boolean);
	const out: string[] = [];
	for (const name of raw) {
		if (name.length < 2 || name.length > 80)
			return { ok: false, error: 'Bank names need 2 to 80 characters each.' };
		if (!out.some((n) => n.toLowerCase() === name.toLowerCase())) out.push(name);
	}
	if (out.length > 10) return { ok: false, error: 'Up to 10 bank names per item.' };
	return { ok: true, value: out };
}

export type Parsed<T> = { ok: true; value: T } | { ok: false; error: string };

export function parseAccountForm(data: FormData): Parsed<{
	name: string;
	institution: string;
	type: AccountType;
	balanceCents: number;
	color: string;
	icon: string;
	note: string | null;
}> {
	const name = str(data, 'name');
	const institution = str(data, 'institution');
	const type = str(data, 'type') as AccountType;
	const balanceCents = parseMoney(str(data, 'balance'));
	const color = str(data, 'color');
	const icon = str(data, 'icon') || 'landmark';
	if (!name) return { ok: false, error: 'Give the account a name.' };
	if (!institution) return { ok: false, error: 'Which bank or institution is it with?' };
	if (!ACCOUNT_TYPES.includes(type)) return { ok: false, error: 'Pick an account type.' };
	if (balanceCents === null)
		return { ok: false, error: 'Enter the balance as a number, like 1250.00.' };
	if (!/^#[0-9a-fA-F]{6}$/.test(color)) return { ok: false, error: 'Pick a colour.' };
	return {
		ok: true,
		value: { name, institution, type, balanceCents, color, icon, note: optional(data, 'note') }
	};
}

export function parseRecurringForm(data: FormData): Parsed<{
	name: string;
	kind: Kind;
	amountCents: number;
	categoryId: string | null;
	accountId: string | null;
	frequency: Frequency;
	anchorDate: string;
	endDate: string | null;
	autoPay: boolean;
	notes: string | null;
	url: string | null;
	bankNames: string[];
	amountVaries: boolean;
}> {
	const name = str(data, 'name');
	const kind = str(data, 'kind') as Kind;
	const amountCents = parseMoney(str(data, 'amount'));
	const frequency = str(data, 'frequency') as Frequency;
	const anchorDate = str(data, 'anchorDate');
	const endDate = optional(data, 'endDate');
	const url = optional(data, 'url');
	if (!name) return { ok: false, error: 'Give it a name.' };
	if (!KINDS.includes(kind)) return { ok: false, error: 'Pick bill, subscription or income.' };
	if (amountCents === null || amountCents <= 0)
		return { ok: false, error: 'Enter an amount greater than zero.' };
	if (!FREQUENCIES.includes(frequency)) return { ok: false, error: 'Pick how often it repeats.' };
	if (!isValidISODate(anchorDate)) return { ok: false, error: 'Pick the first due date.' };
	if (endDate && (!isValidISODate(endDate) || endDate < anchorDate))
		return { ok: false, error: 'The end date must be after the first due date.' };
	if (url && !/^https?:\/\//i.test(url))
		return { ok: false, error: 'Links must start with http:// or https://.' };
	const bankNames = parseBankNames(data);
	if (!bankNames.ok) return bankNames;
	return {
		ok: true,
		value: {
			name,
			kind,
			amountCents,
			categoryId: optional(data, 'categoryId'),
			accountId: optional(data, 'accountId'),
			frequency,
			anchorDate,
			endDate,
			autoPay: data.get('autoPay') === 'on',
			notes: optional(data, 'notes'),
			url,
			bankNames: bankNames.value,
			amountVaries: data.get('amountVaries') === 'on'
		}
	};
}

/** The weekly-budgets sheet: overall target + one amount per category (blank = clear). */
export function parseWeeklyBudgetsForm(data: FormData): Parsed<{
	overallCents: number | null;
	categories: { categoryId: string; amountCents: number | null; had: boolean }[];
}> {
	const overallRaw = str(data, 'overall');
	const overallCents = overallRaw ? parseMoney(overallRaw) : null;
	if (overallRaw && (overallCents === null || overallCents < 0))
		return { ok: false, error: 'Enter the weekly target as a number, like 300.' };
	const categories: { categoryId: string; amountCents: number | null; had: boolean }[] = [];
	for (const id of data.getAll('categoryId').map(String)) {
		if (!/^[0-9a-fA-F-]{8,}$/.test(id)) continue;
		const raw = str(data, `cat_${id}`);
		const cents = raw ? parseMoney(raw) : null;
		if (raw && (cents === null || cents < 0))
			return { ok: false, error: 'Budgets must be numbers, like 120.' };
		categories.push({ categoryId: id, amountCents: cents, had: data.get(`had_${id}`) === '1' });
	}
	return { ok: true, value: { overallCents, categories } };
}

/** Echo submitted values back to the form on failure. */
export function echo(data: FormData): Record<string, string> {
	const out: Record<string, string> = {};
	// repeated keys (one `bankName` per chip) come back newline-joined
	for (const [k, v] of data.entries())
		if (typeof v === 'string') out[k] = k in out ? `${out[k]}\n${v}` : v;
	return out;
}

export function parseExpenseForm(data: FormData): Parsed<ExpenseBody> {
	const name = str(data, 'name');
	const amountCents = parseMoney(str(data, 'amount'));
	const spentOn = str(data, 'spentOn');
	const accountId = optional(data, 'accountId');
	if (!name) return { ok: false, error: 'What did you spend on?' };
	if (amountCents === null || amountCents <= 0)
		return { ok: false, error: 'Enter an amount greater than zero.' };
	if (!isValidISODate(spentOn)) return { ok: false, error: 'Pick the date.' };
	return {
		ok: true,
		value: {
			name,
			amountCents,
			categoryId: optional(data, 'categoryId'),
			accountId,
			spentOn,
			note: optional(data, 'note'),
			deduct: accountId !== null && data.get('deduct') === 'on'
		}
	};
}

export function parseTodoForm(data: FormData): Parsed<TodoBody> {
	const title = str(data, 'title');
	const amountRaw = str(data, 'amount');
	const dueDate = optional(data, 'dueDate');
	if (!title || title.length > 120) return { ok: false, error: 'Give it a short title.' };
	const amountCents = amountRaw ? parseMoney(amountRaw) : null;
	if (amountRaw && amountCents === null)
		return { ok: false, error: 'Enter the amount as a number, like 200.' };
	if (dueDate && !isValidISODate(dueDate)) return { ok: false, error: 'Pick a valid date.' };
	return { ok: true, value: { title, amountCents, dueDate } };
}

export function parseGoalForm(data: FormData): Parsed<GoalBody> {
	const name = str(data, 'name');
	const targetCents = parseMoney(str(data, 'target'));
	const targetDate = optional(data, 'targetDate');
	const color = str(data, 'color');
	const icon = str(data, 'icon') || 'piggy-bank';
	if (!name) return { ok: false, error: 'Name the goal.' };
	if (targetCents === null || targetCents <= 0)
		return { ok: false, error: 'Enter a target amount greater than zero.' };
	if (targetDate && !isValidISODate(targetDate)) return { ok: false, error: 'Pick a valid date.' };
	if (!/^#[0-9a-fA-F]{6}$/.test(color)) return { ok: false, error: 'Pick a colour.' };
	return {
		ok: true,
		value: { name, targetCents, targetDate, accountId: optional(data, 'accountId'), color, icon }
	};
}

export function parseHouseholdSettings(data: FormData): Parsed<HouseholdPatch> {
	const patch: HouseholdPatch = {};
	if (data.has('name')) {
		const name = str(data, 'name');
		if (!name || name.length > 40) return { ok: false, error: 'Give your household a name.' };
		patch.name = name;
	}
	if (data.has('currency')) {
		const currency = (optional(data, 'currencyCustom') ?? str(data, 'currency')).toUpperCase();
		if (!/^[A-Z]{3}$/.test(currency))
			return { ok: false, error: 'Currency must be a 3-letter ISO code like USD or EUR.' };
		patch.currency = currency;
	}
	if (data.has('weekStart')) {
		const ws = str(data, 'weekStart');
		if (ws !== '0' && ws !== '1') return { ok: false, error: 'Pick Sunday or Monday.' };
		patch.weekStart = ws === '1' ? 1 : 0;
	}
	if (data.has('defaultAccountId')) patch.defaultAccountId = optional(data, 'defaultAccountId');
	if (data.has('timezone')) {
		const tz = str(data, 'timezone');
		if (!tz) return { ok: false, error: 'Pick a time zone.' };
		patch.timezone = tz;
	}
	if (data.has('payPeriod')) {
		const pp = str(data, 'payPeriod') as PayPeriod;
		if (!PAY_PERIODS.some((p) => p.value === pp))
			return { ok: false, error: 'Pick how often you get paid.' };
		patch.payPeriod = pp;
		const anchor = optional(data, 'payAnchorDate');
		if (anchor && !isValidISODate(anchor))
			return { ok: false, error: 'Pick the date of a recent payday.' };
		patch.payAnchorDate = anchor;
	}
	// v5: checkbox + marker (an unchecked box is absent from the form data)
	if (data.has('autoIgnoreTransfersForm'))
		patch.autoIgnoreTransfers = data.get('autoIgnoreTransfers') === 'on';
	// v6: Off / Bills only / Everything
	if (data.has('autoMatchLevel')) {
		const level = str(data, 'autoMatchLevel');
		if (level !== 'off' && level !== 'bills' && level !== 'all')
			return { ok: false, error: 'Pick how much the app may match by itself.' };
		patch.autoMatchLevel = level;
	}
	if (data.has('weeklySpend')) {
		const raw = str(data, 'weeklySpend');
		if (!raw) patch.weeklySpendCents = null;
		else {
			const cents = parseMoney(raw);
			if (cents === null || cents < 0)
				return { ok: false, error: 'Enter the weekly target as a number, like 300.' };
			patch.weeklySpendCents = cents;
		}
	}
	return { ok: true, value: patch };
}
