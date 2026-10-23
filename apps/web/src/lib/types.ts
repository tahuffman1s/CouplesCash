// Shapes copied verbatim from docs/API-CONTRACT.md (v1).

export type User = { id: string; email: string; displayName: string; createdAt: string };
export type Member = { id: string; displayName: string; email: string; joinedAt: string };
export type Household = { id: string; name: string; createdAt: string; members: Member[] };
export type AuthResult = {
	user: User;
	household: Household | null;
	accessToken: string;
	accessTokenExpiresIn: number /* seconds */;
	refreshToken: string;
	refreshTokenExpiresAt: string;
};

export type AccountType = 'checking' | 'savings' | 'credit' | 'cash' | 'investment' | 'loan';
export type Account = {
	id: string;
	name: string;
	institution: string;
	type: AccountType;
	balanceCents: number;
	color: string /* hex */;
	icon: string /* lucide icon name */;
	note: string | null;
	createdAt: string;
	updatedAt: string;
};
export type BalanceSnapshot = { balanceCents: number; recordedAt: string; recordedBy: string };

export type Category = {
	id: string;
	name: string;
	color: string;
	icon: string;
	sortOrder: number;
	isDefault: boolean;
};

export type Kind = 'bill' | 'subscription' | 'income';
export type Frequency = 'weekly' | 'biweekly' | 'monthly' | 'quarterly' | 'yearly' | 'once';
export type RecurringItem = {
	id: string;
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
	monthlyEquivalentCents: number;
	nextDueDate: string | null;
	createdAt: string;
	updatedAt: string;
};

export type Occurrence = {
	itemId: string;
	name: string;
	kind: Kind;
	amountCents: number;
	dueDate: string;
	autoPay: boolean;
	categoryId: string | null;
	categoryName: string | null;
	categoryColor: string | null;
	accountId: string | null;
	accountName: string | null;
	paid: { paidAt: string; paidBy: string; amountCents: number } | null;
};

export type Summary = {
	month: string; // YYYY-MM
	netWorthCents: number;
	assetsCents: number;
	liabilitiesCents: number;
	monthlyBillsCents: number;
	monthlySubscriptionsCents: number;
	monthlyIncomeCents: number; // monthly-equivalent of active items
	dueThisMonthCents: number;
	paidThisMonthCents: number;
	remainingThisMonthCents: number;
	byCategory: {
		categoryId: string | null;
		name: string;
		color: string;
		amountCents: number;
		count: number;
	}[]; // monthly-equivalent outflow
	byKind: { kind: Kind; amountCents: number; count: number }[];
	upcoming: Occurrence[]; // next 14 days, unpaid first, max 20
	accounts: Account[];
};

export type CoupleCode = {
	code: string; // "ABCD-EFGH"
	payload: string; // "cc1.<base64url 20 bytes>"
	expiresAt: string;
	household: Household;
};

export type CalendarResponse = { from: string; to: string; occurrences: Occurrence[] };

/** RFC 9457 problem details as returned by the API. */
export type Problem = {
	type?: string;
	title: string;
	status: number;
	detail?: string;
	errors?: Record<string, string[]>;
};

export const ACCOUNT_TYPES: readonly AccountType[] = [
	'checking',
	'savings',
	'cash',
	'investment',
	'credit',
	'loan'
];
export const LIABILITY_TYPES: ReadonlySet<AccountType> = new Set(['credit', 'loan']);
export const KINDS: readonly Kind[] = ['bill', 'subscription', 'income'];
export const FREQUENCIES: readonly Frequency[] = [
	'weekly',
	'biweekly',
	'monthly',
	'quarterly',
	'yearly',
	'once'
];
