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
	/** v7: derived = paydayWeekday (kept for compatibility); older builds send 0 | 1. */
	weekStart: number;
	timezone: string /* IANA */;
	defaultAccountId: string | null;
	payPeriod: PayPeriod;
	payAnchorDate: string | null;
	// v4
	weeklySpendCents: number | null;
	/** v5: auto-ignore both sides of a transfer between linked accounts (default on). */
	autoIgnoreTransfers?: boolean;
	/** v6: what the matcher may do on its own (default `all`). Older builds omit it. */
	autoMatchLevel?: AutoMatchLevel;
	/** v7: 0 = Sunday … 6 = Saturday (default 5 = Friday). The pay week runs payday → the day before the next. */
	paydayWeekday?: number;
};
/** v6: `off` = suggestions only; `bills` = auto-apply bill/income/one-off matches and transfers; `all` = also categories. */
export type AutoMatchLevel = 'off' | 'bills' | 'all';
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
	/** v5: the bank's available balance (mapped accounts); null when unknown. Older builds omit it. */
	availableCents?: number | null;
	/** v5: Σ pending outflows (unreviewed + confirmed) on this account. */
	pendingOutCents?: number;
	/** v5: receives bank or CSV rows. */
	linked?: boolean;
	/** v7: what the account is for in the Friday plan (older builds omit it → `other`). */
	role?: AccountRole;
	ownerUserId?: string | null;
	ownerName?: string | null;
};
/** v7: Bills (exactly one, the only account in the weekly math) · Savings · Fun money (per person) · Other. */
export type AccountRole = 'bills' | 'savings' | 'fun' | 'other';
export const ACCOUNT_ROLES: readonly AccountRole[] = ['bills', 'savings', 'fun', 'other'];
export type BalanceSnapshot = { balanceCents: number; recordedAt: string; recordedBy: string };

export type Category = {
	id: string;
	name: string;
	color: string;
	icon: string;
	sortOrder: number;
	isDefault: boolean;
};

/** v7 adds `transfer` (fun money and other planned moves between your own accounts). */
export type Kind = 'bill' | 'subscription' | 'income' | 'transfer';
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
	/** v4: what the bank calls it (v6: read-only = bankNames[0] ?? null). */
	payeeHint: string | null;
	/** v6: "Bank shows it as" names the user added (≤ 10, 2–80 chars each). Older builds omit it. */
	bankNames?: string[];
	/** v6: normalised descriptions learned from confirmed matches (read-only; forget one by index). */
	bankNamesLearned?: string[];
	/** v6: widens the amount tolerance (utilities, credit-card payments). */
	amountVaries?: boolean;
	/** v7: `transfer` items move money from → to (both in the household). */
	fromAccountId?: string | null;
	toAccountId?: string | null;
	fromAccountName?: string | null;
	toAccountName?: string | null;
};

export type OccurrenceStatus = 'planned' | 'cleared' | 'overdue';
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
	/** v4: paid.amountCents − amountCents when paid, else null (positive = cost more). */
	varianceCents: number | null;
	/** v4: cleared when paid, overdue when unpaid and past due, otherwise planned. */
	status: OccurrenceStatus;
};
export type Paid = {
	paidAt: string;
	paidBy: string | null;
	amountCents: number;
	auto: boolean;
	/**
	 * v3 (assumed, optional): where the payment came from. `bank` = confirmed/auto-matched from the
	 * review inbox. Older API builds omit it; the UI then falls back to `auto` alone.
	 */
	source?: 'manual' | 'autopay' | 'bank' | null;
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
/** What the quick-add sheet and the bill form cycle through (transfers have their own fields). */
export const KINDS: readonly Kind[] = ['bill', 'subscription', 'income'];
export const ALL_KINDS: readonly Kind[] = ['bill', 'subscription', 'income', 'transfer'];
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
	/** v4: "Week ahead" / "left this week" summaries (v7: the payday push replaces them). */
	weekSummaryEnabled: boolean;
	/** v7 (assumed, optional): the one payday push. Older builds omit it → `weekSummaryEnabled`. */
	paydayEnabled?: boolean;
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

// ---- v3 ----------------------------------------------------------------------------------

export type BankLink = {
	id: string;
	provider: 'simplefin' | 'plaid';
	name: string;
	createdAt: string;
	lastSyncedAt: string | null;
	lastError: string | null;
	syncEnabled: boolean;
	bridgeErrors: string[];
	accounts: ExternalAccount[];
	/** v7 Plaid: the bank wants you to sign in again (Reconnect opens Plaid Link in update mode). */
	needsReconnect: boolean;
};
/** v7 GET /banks/plaid: whether this server offers Plaid (keys + DATA_KEY). */
export type PlaidStatus = {
	configured: boolean;
	environment: 'production' | 'sandbox';
	oAuthRedirect: boolean;
};
export type PlaidLinkToken = { linkToken: string; expiration: string };
export type ExternalAccount = {
	id: string;
	externalId: string;
	orgName: string;
	name: string;
	currency: string;
	balanceCents: number;
	availableCents: number | null;
	balanceAt: string;
	accountId: string | null;
	accountName: string | null;
	updatedAt: string;
	/** v7: what the bank feed contained on the last sync (null before the first v7 sync). */
	lastFeed?: FeedStats;
};
export type SyncResult = {
	linkId: string;
	newTransactions: number;
	updatedAccounts: number;
	autoMatched: number;
	errors: string[];
	syncedAt: string;
};
export type InboxStatus = 'pending' | 'confirmed' | 'ignored';
export type Suggestion =
	| { kind: 'category'; categoryId: string; categoryName: string; confidence: number }
	| {
			kind: 'occurrence';
			itemId: string;
			itemName: string;
			itemKind: Kind;
			dueDate: string;
			confidence: number;
	  }
	| { kind: 'transfer'; confidence: number }
	| null;
export type InboxConfirmed = {
	expenseId: string | null;
	itemId: string | null;
	dueDate: string | null;
	by: string | null;
	byName: string | null;
	at: string | null;
	auto: boolean;
	/** Assumed optional extras so confirmed rows can say "Expense in Groceries" / "Paid Rent". */
	categoryName?: string | null;
	itemName?: string | null;
};
export type InboxItem = {
	id: string;
	/**
	 * `manual` only appears on v5 account transactions (typed expenses); `added` is a transaction
	 * added by hand that waits for the bank's own copy.
	 */
	source: 'simplefin' | 'plaid' | 'csv' | 'manual' | 'added';
	accountId: string | null;
	accountName: string | null;
	postedOn: string;
	amountCents: number /* signed */;
	description: string;
	payee: string | null;
	memo: string | null;
	pending: boolean;
	status: InboxStatus;
	suggestion: Suggestion;
	confirmed: InboxConfirmed | null;
	createdAt: string;
	// v5 (optional so v3/v4 API builds keep type-checking at the edges)
	ignoreReason?: IgnoreReason | null;
	transfer?: InboxTransfer | null;
	match?: InboxMatch | null;
	expense?: InboxExpense | null;
	// v6
	matchScore?: number | null;
	matchDetail?: MatchDetail | null;
};
/** v6: why the matcher picked what it picked (0–100 per part). */
export type MatchDetail = {
	name: number;
	amount: number;
	date: number;
	reason: string;
	engine: 'fuzzy' | 'builtin';
};
export type InboxPage = { items: InboxItem[]; nextBefore: string | null };
export type InboxSummary = {
	pending: number;
	suggested: number /* pending with confidence ≥ 70 */;
	lastSyncedAt: string | null;
	linkCount: number;
};
export type BulkResult = { confirmed: number; ignored: number; skipped: number };
export type CsvImportRow = {
	postedOn: string;
	amountCents: number;
	description: string;
	payee?: string;
	memo?: string;
};
export type CsvImportResult = {
	id: string;
	rowCount: number;
	imported: number;
	duplicates: number;
	skipped: number;
	inboxPending: number;
};
export type CsvImportRecord = {
	id: string;
	filename: string;
	rowCount: number;
	imported: number;
	duplicates: number;
	createdAt: string;
	createdByName: string;
};
export const INBOX_STATUSES: readonly InboxStatus[] = ['pending', 'confirmed', 'ignored'];

// ---- v4 ----------------------------------------------------------------------------------

export type BudgetPeriod = 'monthly' | 'weekly';
export type WeekDay = {
	date: string;
	planned: Occurrence[];
	expenses: Expense[];
	plannedOutCents: number;
	plannedInCents: number;
	unplannedCents: number;
};
export type WeekBudgetRow = {
	categoryId: string | null /* null = overall */;
	name: string;
	color: string;
	icon: string;
	budgetCents: number;
	spentCents: number;
	varianceCents: number /* spent − budget, + = over */;
	percent: number;
};
export type WeekBudgetHistory = {
	weekStart: string;
	budgetCents: number;
	spentCents: number;
	varianceCents: number;
}[]; // last 8 weeks incl. current, oldest first
export type WeekBudget = {
	overall: WeekBudgetRow | null;
	categories: WeekBudgetRow[];
	totalBudgetCents: number;
	totalSpentCents: number;
	history: WeekBudgetHistory;
};
export type WeekView = {
	start: string;
	end: string;
	weekStart: 0 | 1;
	isCurrent: boolean;
	isPast: boolean;
	today: string;
	spendableNowCents: number;
	startingBalanceCents: number | null /* past weeks: snapshots at/before start */;
	incomeExpectedCents: number;
	incomeReceivedCents: number;
	plannedOutCents: number;
	paidOutCents: number;
	remainingOutCents: number;
	unplannedCents: number;
	leftAtEndCents: number | null /* null for past weeks without a starting balance */;
	projected: boolean;
	days: WeekDay[];
	overdue: Occurrence[] /* unpaid planned before `start` still open, shown once at the top */;
	budget: WeekBudget;
	/** v6: Σ pending outflows on spendable accounts. Older builds omit it. */
	pendingOutCents?: number;
};

// ---- v5 ----------------------------------------------------------------------------------

export type IgnoreReason = 'manual' | 'transfer' | 'dropped' | 'superseded';
export type InboxTransfer = {
	peerId: string | null;
	peerAccountId: string | null;
	peerAccountName: string | null;
	auto: boolean;
};
export type InboxMatch = { itemId: string; itemName: string; itemKind: Kind; dueDate: string };
export type InboxExpense = {
	expenseId: string;
	categoryId: string | null;
	categoryName: string | null;
};
/** A row on an account's Transactions tab: a bank/CSV row, or a typed expense (`kind: 'manual'`). */
export type AccountTransaction = InboxItem & { kind: 'bank' | 'manual' };
export type TxFilter = 'all' | 'pending' | 'review' | 'matched' | 'transfers';
export type AccountTransactionsPage = {
	items: AccountTransaction[];
	nextBefore: string | null;
	totals: { inCents: number; outCents: number; pendingOutCents: number; count: number };
};
export type MatchCandidate = {
	itemId: string;
	itemName: string;
	kind: Kind;
	dueDate: string;
	amountCents: number;
	paid: Paid | null;
	paidByThis: boolean;
	score: number /* 0–100 */;
};
export type InboxCandidates = { occurrences: MatchCandidate[]; transferPeers: InboxItem[] };
/** `accountId` is null for an unmapped bank row (the name then reads "Other account"). */
export type TransferSide = { accountId: string | null; name: string; transactionId: string };
export type TransferPair = {
	id: string;
	date: string;
	amountCents: number;
	from: TransferSide;
	to: TransferSide;
	auto: boolean;
};
export type TransferFlow = {
	fromAccountId: string | null;
	fromName: string;
	toAccountId: string | null;
	toName: string;
	totalCents: number;
	count: number;
};
export type TransferNet = {
	accountId: string;
	name: string;
	inCents: number;
	outCents: number;
	netCents: number;
};
export type TransferReport = {
	month: string;
	totalCents: number;
	flows: TransferFlow[];
	netByAccount: TransferNet[];
	pairs: TransferPair[];
	oneSided: InboxItem[];
};
export type BillPayment = {
	dueDate: string;
	plannedCents: number;
	paid: Paid | null;
	varianceCents: number | null;
	transaction: {
		id: string;
		postedOn: string;
		amountCents: number;
		description: string;
		accountName: string | null;
	} | null;
};
export type FromTransactionResult = { item: RecurringItem; transaction: InboxItem };
export const TX_FILTERS: readonly { value: TxFilter; label: string }[] = [
	{ value: 'all', label: 'All' },
	{ value: 'pending', label: 'Pending' },
	{ value: 'review', label: 'To review' },
	{ value: 'matched', label: 'Matched' },
	{ value: 'transfers', label: 'Transfers' }
];

// ---- v6 ----------------------------------------------------------------------------------

export type PendingGroup = {
	/** null: an unmapped bank account, or "Other" (rows of an archived account) */
	accountId: string | null;
	accountName: string;
	institution: string;
	balanceCents: number;
	availableCents: number | null;
	pendingCount: number;
	pendingOutCents: number;
	pendingInCents: number;
	items: InboxItem[];
};
export type PendingView = {
	totalOutCents: number;
	totalInCents: number;
	count: number;
	groups: PendingGroup[];
};
export type BillSuggestion = {
	id: string;
	name: string;
	kind: Kind;
	amountCents: number;
	amountVaries: boolean;
	frequency: Frequency;
	nextDue: string;
	bankName: string;
	score: number;
	sampleCount: number;
	lastSeen: string;
};
export type TransactionFeed = {
	/** every in-progress transaction on every account (first page only) */
	pending: InboxItem[];
	/** posted rows + manual expenses, newest first */
	items: InboxItem[];
	nextBefore: string | null;
	toReviewCount: number;
};
export type MatcherStatus = {
	engine: 'fuzzy' | 'builtin';
	reachable: boolean;
	version: string | null;
	lastRunAt: string | null;
	lastAutoMatched: number;
	// v6 API extras (optional so older builds type-check)
	configured?: boolean;
	lastRunEngine?: 'fuzzy' | 'builtin' | null;
	foundBills?: boolean;
	/** why the built-in engine is used, when it is */
	note?: string | null;
};
export const AUTO_MATCH_LEVELS: readonly { value: AutoMatchLevel; label: string; hint: string }[] =
	[
		{ value: 'off', label: 'Off', hint: 'Only suggest; one of you taps to confirm' },
		{
			value: 'bills',
			label: 'Bills only',
			hint: 'Match bills, income, one-offs and transfers by itself'
		},
		{ value: 'all', label: 'Everything', hint: 'Also file regular shops into their usual category' }
	];

// ---- v7 (docs/API-CONTRACT.md § v7 additions) --------------------------------------------

export type PlanLineKind = 'bill' | 'subscription' | 'income' | 'transfer' | 'oneoff';
export type PlanLine = { kind: PlanLineKind; occurrence: Occurrence };
export type ChecklistItem = {
	key: 'paycheck' | 'fun';
	label: string;
	amountCents: number;
	done: boolean;
	auto: boolean;
	transactionId: string | null;
	itemId: string | null;
	dueDate: string | null;
};
export type Sweep = {
	weekStart: string;
	plannedCents: number;
	actualCents: number | null;
	doneAt: string | null;
	auto: boolean;
	/** API notes: the raw (possibly negative) left-at-end the plan was frozen from */
	leftCents?: number;
	transactionId?: string | null;
};
export type FeedStats = {
	pending: number;
	posted: number;
	availableReported: boolean;
	at: string;
} | null;
export type PlanBillsAccount = {
	id: string;
	name: string;
	availableCents: number;
	balanceCents: number;
	availableFromBank: boolean;
	pending: InboxItem[];
	/** the bank's pending outflows (not the added ones) */
	pendingOutCents: number;
	/** signed sum of transactions added by hand the bank hasn't shown yet (already in availableCents) */
	addedCents: number;
	feed: FeedStats;
	/** when the bank's numbers were last refreshed by SimpleFIN (`balance-date`); null without a linked bank */
	balanceAt: string | null;
};
/** POST /transactions: a transaction added by hand (amount signed, negative = money out). */
export type AddTransactionBody = {
	accountId: string;
	amountCents: number;
	description: string;
	postedOn: string;
	force?: boolean;
};
export type PlanAccount = {
	id: string;
	name: string;
	role: AccountRole;
	ownerName: string | null;
	balanceCents: number;
	availableCents: number | null;
};
export type PlanSetup = {
	banksLinked: boolean;
	rolesSet: boolean;
	paycheckKnown: boolean;
	billsAdded: boolean;
	partnerJoined: boolean;
};
export type Plan = {
	start: string;
	end: string;
	nextPayday: string;
	isCurrent: boolean;
	isPayday: boolean;
	today: string;
	/** null → setup needed (no bills account yet) */
	billsAccount: PlanBillsAccount | null;
	accounts: PlanAccount[];
	bills: PlanLine[];
	funMoney: PlanLine[];
	oneOffs: PlanLine[];
	income: PlanLine[];
	/** unpaid remainders */
	billsOutCents: number;
	funOutCents: number;
	oneOffOutCents: number;
	incomeInCents: number;
	/** available − bills − fun − one-offs (+ unpaid income only when it lands before the end) */
	leftAtEndCents: number;
	/** e.g. student loans from savings: shown, not in the math */
	savingsBills: PlanLine[];
	/** payday only (empty otherwise): paycheck and fun money */
	checklist: ChecklistItem[];
	/** this week's savings move, frozen on its last day from Left on <day>; null before then */
	sweep: Sweep | null;
	toReviewCount: number;
	setup: PlanSetup;
};
export const ROLE_LABEL: Record<AccountRole, string> = {
	bills: 'Bills',
	savings: 'Savings',
	fun: 'Fun money',
	other: 'Other'
};
