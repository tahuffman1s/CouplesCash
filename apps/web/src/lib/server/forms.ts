import type { AccountType, Frequency, Kind } from '#lib/types.ts';
import { ACCOUNT_TYPES, ALL_KINDS, FREQUENCIES } from '#lib/types.ts';
import type { HouseholdPatch } from './api';
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
	fromAccountId?: string | null;
	toAccountId?: string | null;
}> {
	const name = str(data, 'name');
	const kind = str(data, 'kind') as Kind;
	const amountCents = parseMoney(str(data, 'amount'));
	const frequency = str(data, 'frequency') as Frequency;
	const anchorDate = str(data, 'anchorDate');
	const endDate = optional(data, 'endDate');
	const url = optional(data, 'url');
	if (!name) return { ok: false, error: 'Give it a name.' };
	if (!ALL_KINDS.includes(kind))
		return { ok: false, error: 'Pick bill, subscription, income or transfer.' };
	const fromAccountId = kind === 'transfer' ? optional(data, 'fromAccountId') : null;
	const toAccountId = kind === 'transfer' ? optional(data, 'toAccountId') : null;
	if (kind === 'transfer' && (!fromAccountId || !toAccountId))
		return { ok: false, error: 'Pick where the money comes from and where it goes.' };
	if (kind === 'transfer' && fromAccountId === toAccountId)
		return { ok: false, error: 'From and to must be different accounts.' };
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
			accountId: kind === 'transfer' ? fromAccountId : optional(data, 'accountId'),
			frequency,
			anchorDate,
			endDate,
			autoPay: kind !== 'transfer' && data.get('autoPay') === 'on',
			notes: optional(data, 'notes'),
			url,
			bankNames: bankNames.value,
			amountVaries: data.get('amountVaries') === 'on',
			...(kind === 'transfer' ? { fromAccountId, toAccountId } : {})
		}
	};
}

/** Echo submitted values back to the form on failure. */
export function echo(data: FormData): Record<string, string> {
	const out: Record<string, string> = {};
	// repeated keys (one `bankName` per chip) come back newline-joined
	for (const [k, v] of data.entries())
		if (typeof v === 'string') out[k] = k in out ? `${out[k]}\n${v}` : v;
	return out;
}

export function parseHouseholdSettings(data: FormData): Parsed<HouseholdPatch> {
	const patch: HouseholdPatch = {};
	if (data.has('name')) {
		const name = str(data, 'name');
		if (!name || name.length > 40) return { ok: false, error: 'Give your household a name.' };
		patch.name = name;
	}
	if (data.has('defaultAccountId')) patch.defaultAccountId = optional(data, 'defaultAccountId');
	if (data.has('timezone')) {
		const tz = str(data, 'timezone');
		if (!tz) return { ok: false, error: 'Pick a time zone.' };
		patch.timezone = tz;
	}
	// v5: checkbox + marker (an unchecked box is absent from the form data)
	if (data.has('autoIgnoreTransfersForm'))
		patch.autoIgnoreTransfers = data.get('autoIgnoreTransfers') === 'on';
	if (data.has('paydayWeekday')) {
		const wd = Number(str(data, 'paydayWeekday'));
		if (!Number.isInteger(wd) || wd < 0 || wd > 6) return { ok: false, error: 'Pick your payday.' };
		patch.paydayWeekday = wd;
	}
	// v6: Off / Bills only / Everything
	if (data.has('autoMatchLevel')) {
		const level = str(data, 'autoMatchLevel');
		if (level !== 'off' && level !== 'bills' && level !== 'all')
			return { ok: false, error: 'Pick how much the app may match by itself.' };
		patch.autoMatchLevel = level;
	}
	return { ok: true, value: patch };
}
