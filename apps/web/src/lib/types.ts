// Shapes copied verbatim from docs/API-CONTRACT.md (v1 + v2 additions).

export type User = { id: string; email: string; displayName: string; createdAt: string };
export type Member = { id: string; displayName: string; email: string; joinedAt: string };
export type PayPeriod = 'weekly' | 'biweekly' | 'bimonthly' | 'monthly';
export type Household = {
	id: string;
	name: string;
	createdAt: string;
	members: Member[];
	// v2 settings
	currency: string /* ISO 4217, default USD */;
	weekStart: 0 | 1;
	timezone: string /* IANA */;
	defaultAccountId: string | null;
	payPeriod: PayPeriod;
	payAnchorDate: string | null;
};
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
	paid: Paid | null;
};
export type Paid = { paidAt: string; paidBy: string | null; amountCents: number; auto: boolean };

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

// ---- v2 ----------------------------------------------------------------------------------

export type ActivityEvent = {
	id: string;
	kind: string;
	summary: string;
	actorId: string | null;
	actorName: string | null;
	entityType: string;
	entityId: string | null;
	data: Record<string, unknown>;
	createdAt: string;
};
export type ActivityPage = { events: ActivityEvent[]; nextBefore: string | null };

export type Expense = {
	id: string;
	name: string;
	amountCents: number;
	categoryId: string | null;
	accountId: string | null;
	spentOn: string;
	note: string | null;
	deducted: boolean;
	createdBy: string;
	createdAt: string;
	updatedAt: string;
};

export type BudgetRow = {
	categoryId: string;
	name: string;
	color: string;
	icon: string;
	budgetCents: number | null;
	recurringCents: number;
	expensesCents: number;
	spentCents: number;
	remainingCents: number | null;
	percent: number | null;
};
export type BudgetsResponse = {
	month: string;
	rows: BudgetRow[];
	totalBudgetCents: number;
	totalSpentCents: number;
};

export type PushSubscriptionInfo = {
	id: string;
	userAgent: string;
	createdAt: string;
	lastUsedAt: string | null;
};
export type PushPrefs = {
	remindersEnabled: boolean;
	daysBefore: number[];
	hourLocal: number;
	activityEnabled: boolean;
	subscriptions: PushSubscriptionInfo[];
};

export type Todo = {
	id: string;
	title: string;
	amountCents: number | null;
	dueDate: string | null;
	sortOrder: number;
	doneAt: string | null;
	doneBy: string | null;
	doneByName: string | null;
	createdBy: string;
	createdAt: string;
};

export type Goal = {
	id: string;
	name: string;
	targetCents: number;
	targetDate: string | null;
	accountId: string | null;
	accountName: string | null;
	color: string;
	icon: string;
	currentCents: number;
	percent: number;
	monthlyPaceCents: number | null /* avg change/month, last 90d */;
	projectedDate: string | null;
	monthlyNeededCents: number | null /* to hit targetDate */;
	history: { at: string; value: number }[];
	createdAt: string;
};

export type Passkey = {
	id: string;
	name: string;
	createdAt: string;
	lastUsedAt: string | null;
	backedUp: boolean;
};
export type PasskeyConfig = { enabled: boolean; rpId: string };

export type PayPeriodView = {
	period: PayPeriod;
	start: string;
	end: string /* inclusive */;
	nextPayday: string;
	daysLeft: number;
	expectedIncomeCents: number;
	dueCents: number;
	paidCents: number;
	remainingCents: number;
	spendableCents: number /* checking + cash balances − remaining */;
	occurrences: Occurrence[];
	expensesCents: number;
};

export type MonthReport = {
	month: string;
	summary: Summary;
	previousNetWorthCents: number | null;
	netWorthDeltaCents: number | null;
	expensesTotalCents: number;
	topExpenses: Expense[];
	budgets: BudgetRow[];
	autoPaid: Occurrence[];
	paidCount: number;
	unpaidCount: number;
	generatedAt: string;
};

export const PAY_PERIODS: readonly { value: PayPeriod; label: string }[] = [
	{ value: 'weekly', label: 'Every week' },
	{ value: 'biweekly', label: 'Every two weeks' },
	{ value: 'bimonthly', label: 'Twice a month' },
	{ value: 'monthly', label: 'Monthly' }
];
export const PAY_PERIOD_LABEL: Record<PayPeriod, string> = Object.fromEntries(
	PAY_PERIODS.map((p) => [p.value, p.label])
) as Record<PayPeriod, string>;

/** Common ISO 4217 codes offered in the household settings select (free text is allowed too). */
export const COMMON_CURRENCIES: readonly { code: string; name: string }[] = [
	{ code: 'USD', name: 'US dollar' },
	{ code: 'EUR', name: 'Euro' },
	{ code: 'GBP', name: 'British pound' },
	{ code: 'CAD', name: 'Canadian dollar' },
	{ code: 'AUD', name: 'Australian dollar' },
	{ code: 'NZD', name: 'New Zealand dollar' },
	{ code: 'CHF', name: 'Swiss franc' },
	{ code: 'SEK', name: 'Swedish krona' },
	{ code: 'NOK', name: 'Norwegian krone' },
	{ code: 'DKK', name: 'Danish krone' },
	{ code: 'JPY', name: 'Japanese yen' },
	{ code: 'INR', name: 'Indian rupee' },
	{ code: 'MXN', name: 'Mexican peso' },
	{ code: 'BRL', name: 'Brazilian real' },
	{ code: 'ZAR', name: 'South African rand' },
	{ code: 'SGD', name: 'Singapore dollar' }
];
