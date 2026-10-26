import type { AccountType, Frequency, Kind } from '#lib/types.ts';

export const ACCOUNT_TYPE_LABEL: Record<AccountType, string> = {
	checking: 'Checking',
	savings: 'Savings',
	cash: 'Cash',
	investment: 'Investments',
	credit: 'Credit cards',
	loan: 'Loans'
};

export const ACCOUNT_TYPE_ICON: Record<AccountType, string> = {
	checking: 'landmark',
	savings: 'piggy-bank',
	cash: 'banknote',
	investment: 'trending-up',
	credit: 'credit-card',
	loan: 'hand-coins'
};

export const KIND_LABEL: Record<Kind, string> = {
	bill: 'Bill',
	subscription: 'Subscription',
	income: 'Income',
	transfer: 'Transfer'
};

export const KIND_LABEL_PLURAL: Record<Kind, string> = {
	bill: 'Bills',
	subscription: 'Subscriptions',
	income: 'Income',
	transfer: 'Transfers'
};

export const FREQUENCY_LABEL: Record<Frequency, string> = {
	weekly: 'Weekly',
	biweekly: 'Every 2 weeks',
	monthly: 'Monthly',
	quarterly: 'Quarterly',
	yearly: 'Yearly',
	once: 'One time'
};

/** Short suffix for amounts: "$12 /mo". */
export const FREQUENCY_SUFFIX: Record<Frequency, string> = {
	weekly: '/wk',
	biweekly: '/2wk',
	monthly: '/mo',
	quarterly: '/qtr',
	yearly: '/yr',
	once: 'once'
};

/** Monthly-equivalent factor per the contract (weekly ×52/12 …). */
export function monthlyEquivalent(amountCents: number, frequency: Frequency): number {
	switch (frequency) {
		case 'weekly':
			return Math.round((amountCents * 52) / 12);
		case 'biweekly':
			return Math.round((amountCents * 26) / 12);
		case 'monthly':
			return amountCents;
		case 'quarterly':
			return Math.round(amountCents / 3);
		case 'yearly':
			return Math.round(amountCents / 12);
		case 'once':
		default:
			return 0;
	}
}

/** A readable palette of swatches for pickers (validated hues, see dataviz palette). */
export const SWATCHES = [
	'#2a78d6',
	'#eb6834',
	'#1baf7a',
	'#eda100',
	'#e87ba4',
	'#008300',
	'#6250d6',
	'#e34948',
	'#0e8a9e',
	'#8a6d3b'
] as const;

export const ACCOUNT_ICONS = [
	'landmark',
	'piggy-bank',
	'banknote',
	'wallet',
	'credit-card',
	'trending-up',
	'hand-coins',
	'coins',
	'house',
	'car'
] as const;

/** Icon per recurring kind, for suggestion chips and compact rows. */
export const KIND_ICON: Record<Kind, string> = {
	bill: 'receipt',
	subscription: 'repeat',
	income: 'banknote',
	transfer: 'arrow-left-right'
};

/** Guesses an account type from a bank's own account name ("Freedom Visa" → credit). */
export function guessAccountType(name: string): AccountType {
	const n = name.toLowerCase();
	if (/credit|card|visa|mastercard|amex|discover/.test(n)) return 'credit';
	if (/saving|sav\b|money market|cd\b/.test(n)) return 'savings';
	if (/loan|mortgage|auto fin|student/.test(n)) return 'loan';
	if (/invest|brokerage|401|ira|roth/.test(n)) return 'investment';
	return 'checking';
}

/** "−$12.49" / "+$3,200.00" for signed transaction amounts (U+2212 minus). */
export function signedAmount(cents: number, format: (c: number) => string): string {
	return `${cents < 0 ? '\u2212' : '+'}${format(Math.abs(cents))}`;
}
