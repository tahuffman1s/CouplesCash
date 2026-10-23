/**
 * In-memory mock of the CouplesCash API (enough of docs/API-CONTRACT.md, v1 + v2, for e2e tests).
 * Started by Playwright as a webServer on port 8099. Run manually: node e2e/mock-api.ts
 *
 * Test-only helpers (never in the real API):
 *   POST /__reset                 wipe everything
 *   POST /__mock/autopay?date=    run the autopay scheduler for that day (default: today)
 *   POST /__mock/bank-sync        run the bank-sync scheduler phase for every link (ignores cooldown)
 * Env: MOCK_PUSH=1 serves a fake VAPID key (default: 503 push_not_configured).
 *      MOCK_BANKS_DISABLED=1 → POST /v1/banks/simplefin/claim answers 503 bank_sync_not_configured
 *      (the setup token `demo-not-configured` does the same per request, so one e2e can cover it).
 *
 * v3: a fake SimpleFIN Bridge lives in memory. The setup token `demo-token` claims a link with two
 * accounts (checking + credit card) and a handful of transactions; anything else is 400.
 */
import { createServer, type IncomingMessage, type ServerResponse } from 'node:http';
import { createHash, randomBytes, randomUUID } from 'node:crypto';
import { periodFor } from '../src/lib/utils/payperiod.ts';

type User = {
	id: string;
	email: string;
	displayName: string;
	password: string;
	createdAt: string;
	householdId: string | null;
};
type Member = { id: string; displayName: string; email: string; joinedAt: string };
type Household = {
	id: string;
	name: string;
	createdAt: string;
	memberIds: string[];
	joinedAt: Record<string, string>;
	currency: string;
	weekStart: 0 | 1;
	timezone: string;
	defaultAccountId: string | null;
	payPeriod: 'weekly' | 'biweekly' | 'bimonthly' | 'monthly';
	payAnchorDate: string | null;
};
type Account = {
	id: string;
	householdId: string;
	name: string;
	institution: string;
	type: string;
	balanceCents: number;
	color: string;
	icon: string;
	note: string | null;
	createdAt: string;
	updatedAt: string;
	archived: boolean;
};
type Snapshot = { balanceCents: number; recordedAt: string; recordedBy: string };
type Category = {
	id: string;
	householdId: string;
	name: string;
	color: string;
	icon: string;
	sortOrder: number;
	isDefault: boolean;
};
type Item = {
	id: string;
	householdId: string;
	name: string;
	kind: string;
	amountCents: number;
	categoryId: string | null;
	accountId: string | null;
	frequency: string;
	anchorDate: string;
	endDate: string | null;
	autoPay: boolean;
	notes: string | null;
	url: string | null;
	createdAt: string;
	updatedAt: string;
	archived: boolean;
};
type Payment = {
	paidAt: string;
	paidBy: string | null;
	amountCents: number;
	auto: boolean;
	source: 'manual' | 'autopay' | 'bank';
};
type Code = {
	bytesHash: string;
	codeHash: string;
	code: string;
	payload: string;
	householdId: string;
	expiresAt: number;
	redeemed: boolean;
};
type Activity = {
	id: string;
	householdId: string;
	kind: string;
	summary: string;
	actorId: string | null;
	entityType: string;
	entityId: string | null;
	data: Record<string, unknown>;
	createdAt: string;
};
type Expense = {
	id: string;
	householdId: string;
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
type Todo = {
	id: string;
	householdId: string;
	title: string;
	amountCents: number | null;
	dueDate: string | null;
	sortOrder: number;
	doneAt: string | null;
	doneBy: string | null;
	createdBy: string;
	createdAt: string;
};
type Goal = {
	id: string;
	householdId: string;
	name: string;
	targetCents: number;
	targetDate: string | null;
	accountId: string | null;
	savedCents: number;
	color: string;
	icon: string;
	createdBy: string;
	createdAt: string;
	archived: boolean;
	contributions: { at: string; amountCents: number }[];
};
type PushSub = {
	id: string;
	userId: string;
	endpoint: string;
	p256dh: string;
	auth: string;
	userAgent: string;
	createdAt: string;
	lastUsedAt: string | null;
};
type Prefs = {
	remindersEnabled: boolean;
	daysBefore: number[];
	hourLocal: number;
	activityEnabled: boolean;
};

// ---- v3 types -----------------------------------------------------------------------------
type BankLinkRec = {
	id: string;
	householdId: string;
	provider: 'simplefin';
	name: string;
	createdBy: string;
	createdAt: string;
	lastSyncedAt: string | null;
	lastManualSyncAt: string | null;
	lastError: string | null;
	syncEnabled: boolean;
	bridgeErrors: string[];
};
type ExtAccount = {
	id: string;
	bankLinkId: string;
	householdId: string;
	externalId: string;
	orgName: string;
	name: string;
	currency: string;
	balanceCents: number;
	availableCents: number | null;
	balanceAt: string;
	accountId: string | null;
	createdAt: string;
	updatedAt: string;
};
type Suggestion =
	| { kind: 'category'; categoryId: string; categoryName: string; confidence: number }
	| {
			kind: 'occurrence';
			itemId: string;
			itemName: string;
			itemKind: string;
			dueDate: string;
			confidence: number;
	  }
	| { kind: 'transfer'; confidence: number }
	| null;
type Confirmed = {
	expenseId: string | null;
	itemId: string | null;
	dueDate: string | null;
	by: string | null;
	at: string | null;
	auto: boolean;
	categoryName: string | null;
	itemName: string | null;
};
type Tx = {
	id: string;
	householdId: string;
	source: 'simplefin' | 'csv';
	externalAccountId: string | null;
	accountId: string | null;
	externalId: string | null;
	fingerprint: string;
	postedOn: string;
	amountCents: number;
	description: string;
	payee: string | null;
	memo: string | null;
	pending: boolean;
	status: 'pending' | 'confirmed' | 'ignored';
	suggestion: Suggestion;
	confidence: number;
	confirmed: Confirmed | null;
	createdAt: string;
};
type PayeeRule = {
	householdId: string;
	payeeNorm: string;
	categoryId: string | null;
	itemId: string | null;
	hits: number;
};
type CsvImportRec = {
	id: string;
	householdId: string;
	accountId: string;
	filename: string;
	rowCount: number;
	imported: number;
	duplicates: number;
	createdBy: string;
	createdAt: string;
};
/** What the fake Bridge serves: protocol-shaped but already in cents / ISO dates. */
type BridgeTx = {
	id: string;
	posted: string;
	amount: number;
	description: string;
	payee?: string;
	memo?: string;
	pending?: boolean;
};
type BridgeAccount = {
	id: string;
	org: string;
	name: string;
	currency: string;
	balance: number;
	available: number | null;
	transactions: BridgeTx[];
};

const users = new Map<string, User>();
const households = new Map<string, Household>();
const accounts = new Map<string, Account>();
const snapshots = new Map<string, Snapshot[]>();
const categories = new Map<string, Category>();
const items = new Map<string, Item>();
const payments = new Map<string, Payment>(); // `${itemId}:${date}`
const refreshTokens = new Map<string, { userId: string; family: string; revoked: boolean }>();
const codes: Code[] = [];
const activity: Activity[] = [];
const expenses = new Map<string, Expense>();
const budgets = new Map<string, number>(); // `${hh}:${categoryId}`
const todos = new Map<string, Todo>();
const goals = new Map<string, Goal>();
const pushSubs = new Map<string, PushSub>(); // by endpoint
const prefs = new Map<string, Prefs>(); // by userId
const bankLinks = new Map<string, BankLinkRec>();
const extAccounts = new Map<string, ExtAccount>();
const txs = new Map<string, Tx>();
const payeeRules = new Map<string, PayeeRule>(); // `${hh}:${payeeNorm}`
const csvImports = new Map<string, CsvImportRec>();
const bridge = new Map<string, BridgeAccount[]>(); // by bank link id

const PORT = Number(process.env.MOCK_API_PORT ?? 8099);
const ACCESS_TTL = Number(process.env.MOCK_ACCESS_TTL ?? 900);
const PUSH_ENABLED = process.env.MOCK_PUSH === '1';
const BANKS_DISABLED = process.env.MOCK_BANKS_DISABLED === '1';
const SYNC_COOLDOWN_MS = 15 * 60_000;

const b64url = (buf: Buffer | string) => Buffer.from(buf).toString('base64url');
const sha = (s: string | Buffer) => createHash('sha256').update(s).digest();
const nowIso = () => new Date().toISOString();

function jwt(user: User): string {
	const header = b64url(JSON.stringify({ alg: 'HS256', typ: 'JWT' }));
	const now = Math.floor(Date.now() / 1000);
	const payload: Record<string, unknown> = {
		sub: user.id,
		email: user.email,
		name: user.displayName,
		iat: now,
		exp: now + ACCESS_TTL,
		jti: randomUUID(),
		iss: 'couplescash',
		aud: 'couplescash'
	};
	if (user.householdId) payload.hh = user.householdId;
	return `${header}.${b64url(JSON.stringify(payload))}.${b64url(sha(header).subarray(0, 16))}`;
}

function householdView(h: Household | null) {
	if (!h) return null;
	const members: Member[] = h.memberIds.map((id) => {
		const u = users.get(id)!;
		return { id: u.id, displayName: u.displayName, email: u.email, joinedAt: h.joinedAt[id] };
	});
	return {
		id: h.id,
		name: h.name,
		createdAt: h.createdAt,
		members,
		currency: h.currency,
		weekStart: h.weekStart,
		timezone: h.timezone,
		defaultAccountId: h.defaultAccountId,
		payPeriod: h.payPeriod,
		payAnchorDate: h.payAnchorDate
	};
}

function userView(u: User) {
	return { id: u.id, email: u.email, displayName: u.displayName, createdAt: u.createdAt };
}

function authResult(u: User) {
	const refreshToken = randomBytes(32).toString('base64url');
	refreshTokens.set(refreshToken, { userId: u.id, family: randomUUID(), revoked: false });
	return {
		user: userView(u),
		household: householdView(u.householdId ? households.get(u.householdId)! : null),
		accessToken: jwt(u),
		accessTokenExpiresIn: ACCESS_TTL,
		refreshToken,
		refreshTokenExpiresAt: new Date(Date.now() + 30 * 86400_000).toISOString()
	};
}

const DEFAULT_CATEGORIES: [string, string, string][] = [
	['Housing', '#2a78d6', 'house'],
	['Utilities', '#eb6834', 'zap'],
	['Insurance', '#1baf7a', 'shield'],
	['Transportation', '#eda100', 'car'],
	['Groceries', '#e87ba4', 'shopping-cart'],
	['Subscriptions', '#6250d6', 'repeat'],
	['Debt', '#e34948', 'credit-card'],
	['Health', '#008300', 'stethoscope'],
	['Kids & Pets', '#0e8a9e', 'paw-print'],
	['Fun', '#c2410c', 'party-popper'],
	['Income', '#15803d', 'banknote'],
	['Other', '#8a6d3b', 'ellipsis']
];

function createHousehold(name: string, owner: User): Household {
	const h: Household = {
		id: randomUUID(),
		name: name || `${owner.displayName}'s household`,
		createdAt: nowIso(),
		memberIds: [owner.id],
		joinedAt: { [owner.id]: nowIso() },
		currency: 'USD',
		weekStart: 0,
		timezone: 'UTC',
		defaultAccountId: null,
		payPeriod: 'monthly',
		payAnchorDate: null
	};
	households.set(h.id, h);
	owner.householdId = h.id;
	DEFAULT_CATEGORIES.forEach(([n, color, icon], i) => {
		const c: Category = {
			id: randomUUID(),
			householdId: h.id,
			name: n,
			color,
			icon,
			sortOrder: i,
			isDefault: true
		};
		categories.set(c.id, c);
	});
	return h;
}

// ---- helpers ----------------------------------------------------------------------------
const pad = (n: number) => (n < 10 ? `0${n}` : `${n}`);
const toIso = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
const parse = (s: string) => {
	const [y, m, d] = s.split('-').map(Number);
	return new Date(y, m - 1, d);
};
const todayIso = () => toIso(new Date());
function money(hh: string, cents: number): string {
	const cur = households.get(hh)?.currency ?? 'USD';
	try {
		return new Intl.NumberFormat('en-US', { style: 'currency', currency: cur }).format(cents / 100);
	} catch {
		return `${cur} ${(cents / 100).toFixed(2)}`;
	}
}
function record(
	hh: string,
	actor: User | null,
	kind: string,
	entityType: string,
	entityId: string | null,
	summary: string,
	data: Record<string, unknown> = {}
) {
	activity.push({
		id: randomUUID(),
		householdId: hh,
		kind,
		summary,
		actorId: actor?.id ?? null,
		entityType,
		entityId,
		data,
		createdAt: nowIso()
	});
}
function activityView(a: Activity) {
	const actor = a.actorId ? users.get(a.actorId) : null;
	return {
		id: a.id,
		kind: a.kind,
		summary: a.summary,
		actorId: a.actorId,
		actorName: actor?.displayName ?? null,
		entityType: a.entityType,
		entityId: a.entityId,
		data: a.data,
		createdAt: a.createdAt
	};
}
function setBalance(a: Account, cents: number, by: string) {
	a.balanceCents = cents;
	a.updatedAt = nowIso();
	snapshots.set(a.id, [
		{ balanceCents: a.balanceCents, recordedAt: a.updatedAt, recordedBy: by },
		...(snapshots.get(a.id) ?? [])
	]);
}

// ---- recurrence -------------------------------------------------------------------------
function addMonthsClamp(anchor: Date, n: number): Date {
	const y = anchor.getFullYear() + Math.floor((anchor.getMonth() + n) / 12);
	const m = (((anchor.getMonth() + n) % 12) + 12) % 12;
	const dim = new Date(y, m + 1, 0).getDate();
	return new Date(y, m, Math.min(anchor.getDate(), dim));
}
function expand(item: Item, from: string, to: string): string[] {
	const out: string[] = [];
	const anchor = parse(item.anchorDate);
	const end = item.endDate ?? '9999-12-31';
	const push = (d: Date) => {
		const iso = toIso(d);
		if (iso >= from && iso <= to && iso <= end) out.push(iso);
		return iso;
	};
	if (item.frequency === 'once') {
		push(anchor);
		return out;
	}
	for (let i = 0; i < 2000; i++) {
		let d: Date;
		if (item.frequency === 'weekly' || item.frequency === 'biweekly') {
			d = new Date(anchor);
			d.setDate(anchor.getDate() + i * (item.frequency === 'weekly' ? 7 : 14));
		} else {
			d = addMonthsClamp(
				anchor,
				i * (item.frequency === 'monthly' ? 1 : item.frequency === 'quarterly' ? 3 : 12)
			);
		}
		const iso = push(d);
		if (iso > to || iso > end) break;
	}
	return out;
}
function monthlyEq(amount: number, f: string): number {
	switch (f) {
		case 'weekly':
			return Math.round((amount * 52) / 12);
		case 'biweekly':
			return Math.round((amount * 26) / 12);
		case 'monthly':
			return amount;
		case 'quarterly':
			return Math.round(amount / 3);
		case 'yearly':
			return Math.round(amount / 12);
		default:
			return 0;
	}
}
function nextDue(item: Item): string | null {
	const today = todayIso();
	const far = toIso(addMonthsClamp(new Date(), 24));
	return expand(item, today, far)[0] ?? null;
}
function itemView(i: Item) {
	return {
		id: i.id,
		name: i.name,
		kind: i.kind,
		amountCents: i.amountCents,
		categoryId: i.categoryId,
		accountId: i.accountId,
		frequency: i.frequency,
		anchorDate: i.anchorDate,
		endDate: i.endDate,
		autoPay: i.autoPay,
		notes: i.notes,
		url: i.url,
		monthlyEquivalentCents: monthlyEq(i.amountCents, i.frequency),
		nextDueDate: nextDue(i),
		createdAt: i.createdAt,
		updatedAt: i.updatedAt
	};
}
function occurrence(i: Item, date: string) {
	const c = i.categoryId ? categories.get(i.categoryId) : undefined;
	const a = i.accountId ? accounts.get(i.accountId) : undefined;
	return {
		itemId: i.id,
		name: i.name,
		kind: i.kind,
		amountCents: i.amountCents,
		dueDate: date,
		autoPay: i.autoPay,
		categoryId: c?.id ?? null,
		categoryName: c?.name ?? null,
		categoryColor: c?.color ?? null,
		accountId: a?.id ?? null,
		accountName: a?.name ?? null,
		paid: payments.get(`${i.id}:${date}`) ?? null
	};
}
type Occ = ReturnType<typeof occurrence>;
function calendar(hh: string, from: string, to: string): Occ[] {
	const out: Occ[] = [];
	for (const i of items.values()) {
		if (i.householdId !== hh || i.archived) continue;
		for (const d of expand(i, from, to)) out.push(occurrence(i, d));
	}
	return out.sort((a, b) => a.dueDate.localeCompare(b.dueDate) || a.name.localeCompare(b.name));
}
function accountView(a: Account) {
	const { householdId: _h, archived: _a, ...rest } = a;
	return rest;
}
function categoryView(c: Category) {
	const { householdId: _h, ...rest } = c;
	return rest;
}
function expenseView(e: Expense) {
	const { householdId: _h, ...rest } = e;
	return rest;
}
function todoView(t: Todo) {
	const { householdId: _h, ...rest } = t;
	return { ...rest, doneByName: t.doneBy ? (users.get(t.doneBy)?.displayName ?? null) : null };
}
function monthRange(month: string): [string, string] {
	const first = `${month}-01`;
	const d = parse(first);
	return [first, toIso(new Date(d.getFullYear(), d.getMonth() + 1, 0))];
}
function hhExpenses(hh: string, from: string, to: string): Expense[] {
	return [...expenses.values()]
		.filter((e) => e.householdId === hh && e.spentOn >= from && e.spentOn <= to)
		.sort((a, b) => b.spentOn.localeCompare(a.spentOn) || b.createdAt.localeCompare(a.createdAt));
}
function netWorthAt(hh: string, atIso: string | null): number {
	let total = 0;
	for (const a of accounts.values()) {
		if (a.householdId !== hh || a.archived) continue;
		let balance = a.balanceCents;
		if (atIso) {
			const snap = (snapshots.get(a.id) ?? []).find((s) => s.recordedAt <= atIso);
			if (!snap) continue; // account did not exist yet
			balance = snap.balanceCents;
		}
		total += a.type === 'credit' || a.type === 'loan' ? -balance : balance;
	}
	return total;
}

function summaryFor(hh: string, month: string) {
	const [first, last] = monthRange(month);
	const acc = [...accounts.values()].filter((a) => a.householdId === hh && !a.archived);
	const liab = (t: string) => t === 'credit' || t === 'loan';
	const assetsCents = acc.filter((a) => !liab(a.type)).reduce((s, a) => s + a.balanceCents, 0);
	const liabilitiesCents = acc.filter((a) => liab(a.type)).reduce((s, a) => s + a.balanceCents, 0);
	const active = [...items.values()].filter((i) => i.householdId === hh && !i.archived);
	const eq = (k: string) =>
		active
			.filter((i) => i.kind === k)
			.reduce((s, i) => s + monthlyEq(i.amountCents, i.frequency), 0);
	const occ = calendar(hh, first, last);
	const out = occ.filter((o) => o.kind !== 'income');
	const dueThisMonthCents = out.reduce((s, o) => s + o.amountCents, 0);
	const paidThisMonthCents = out
		.filter((o) => o.paid)
		.reduce((s, o) => s + (o.paid?.amountCents ?? 0), 0);
	type Row = {
		categoryId: string | null;
		name: string;
		color: string;
		amountCents: number;
		count: number;
	};
	const byCat = new Map<string | null, Row>();
	const bucket = (categoryId: string | null) => {
		const c = categoryId ? categories.get(categoryId) : undefined;
		const key = c?.id ?? null;
		const row = byCat.get(key) ?? {
			categoryId: key,
			name: c?.name ?? 'Uncategorised',
			color: c?.color ?? '#9ca3af',
			amountCents: 0,
			count: 0
		};
		byCat.set(key, row);
		return row;
	};
	for (const i of active) {
		if (i.kind === 'income') continue;
		const row = bucket(i.categoryId);
		row.amountCents += monthlyEq(i.amountCents, i.frequency);
		row.count += 1;
	}
	// v2: one-off expenses in the month count too
	for (const e of hhExpenses(hh, first, last)) {
		const row = bucket(e.categoryId);
		row.amountCents += e.amountCents;
		row.count += 1;
	}
	const byKind = ['bill', 'subscription', 'income'].map((kind) => ({
		kind,
		amountCents: eq(kind),
		count: active.filter((i) => i.kind === kind).length
	}));
	const today = todayIso();
	const in14 = toIso(new Date(Date.now() + 14 * 86400_000));
	const upcoming = calendar(hh, today, in14)
		.sort((a, b) => Number(!!a.paid) - Number(!!b.paid) || a.dueDate.localeCompare(b.dueDate))
		.slice(0, 20);
	return {
		month,
		netWorthCents: assetsCents - liabilitiesCents,
		assetsCents,
		liabilitiesCents,
		monthlyBillsCents: eq('bill'),
		monthlySubscriptionsCents: eq('subscription'),
		monthlyIncomeCents: eq('income'),
		dueThisMonthCents,
		paidThisMonthCents,
		remainingThisMonthCents: dueThisMonthCents - paidThisMonthCents,
		byCategory: [...byCat.values()].sort((a, b) => b.amountCents - a.amountCents),
		byKind,
		upcoming,
		accounts: acc.map(accountView)
	};
}

function budgetRows(hh: string, month: string) {
	const [first, last] = monthRange(month);
	const active = [...items.values()].filter((i) => i.householdId === hh && !i.archived);
	const monthExpenses = hhExpenses(hh, first, last);
	const rows = [...categories.values()]
		.filter((c) => c.householdId === hh)
		.sort((a, b) => a.sortOrder - b.sortOrder)
		.map((c) => budgetRow(hh, c, active, monthExpenses));
	return {
		month,
		rows,
		totalBudgetCents: rows.reduce((s, r) => s + (r.budgetCents ?? 0), 0),
		totalSpentCents: rows
			.filter((r) => r.budgetCents !== null)
			.reduce((s, r) => s + r.spentCents, 0)
	};
}
function budgetRow(hh: string, c: Category, active: Item[], monthExpenses: Expense[]) {
	const recurringCents = active
		.filter((i) => i.kind !== 'income' && i.categoryId === c.id)
		.reduce((s, i) => s + monthlyEq(i.amountCents, i.frequency), 0);
	const expensesCents = monthExpenses
		.filter((e) => e.categoryId === c.id)
		.reduce((s, e) => s + e.amountCents, 0);
	const spentCents = recurringCents + expensesCents;
	const budgetCents = budgets.get(`${hh}:${c.id}`) ?? null;
	return {
		categoryId: c.id,
		name: c.name,
		color: c.color,
		icon: c.icon,
		budgetCents,
		recurringCents,
		expensesCents,
		spentCents,
		remainingCents: budgetCents === null ? null : budgetCents - spentCents,
		percent:
			budgetCents === null
				? null
				: budgetCents > 0
					? Math.round((spentCents / budgetCents) * 100)
					: spentCents > 0
						? 100
						: 0
	};
}

function goalView(g: Goal) {
	const account = g.accountId ? accounts.get(g.accountId) : undefined;
	let history: { at: string; value: number }[];
	let currentCents: number;
	if (account) {
		history = [...(snapshots.get(account.id) ?? [])]
			.reverse()
			.map((s) => ({ at: s.recordedAt, value: s.balanceCents }));
		currentCents = account.balanceCents;
	} else {
		// the real API seeds a manual goal's history with a zero point at creation
		let running = 0;
		history = [
			{ at: g.createdAt, value: 0 },
			...g.contributions.map((c) => ({ at: c.at, value: (running += c.amountCents) }))
		];
		currentCents = g.savedCents;
	}
	history = history.slice(-90);
	const cutoff = new Date(Date.now() - 90 * 86400_000).toISOString();
	const recent = history.filter((h) => h.at >= cutoff);
	let monthlyPaceCents: number | null = null;
	if (recent.length >= 2) {
		const firstP = recent[0];
		const lastP = recent[recent.length - 1];
		const days = (new Date(lastP.at).getTime() - new Date(firstP.at).getTime()) / 86400_000;
		if (days >= 7) monthlyPaceCents = Math.round((lastP.value - firstP.value) / (days / 30.4375));
	}
	const remaining = g.targetCents - currentCents;
	let projectedDate: string | null = null;
	if (remaining > 0 && monthlyPaceCents !== null && monthlyPaceCents > 0) {
		const months = remaining / monthlyPaceCents;
		projectedDate = toIso(new Date(Date.now() + months * 30.4375 * 86400_000));
	}
	let monthlyNeededCents: number | null = null;
	if (remaining > 0 && g.targetDate) {
		const months = Math.max(
			1 / 30.4375,
			(parse(g.targetDate).getTime() - Date.now()) / (30.4375 * 86400_000)
		);
		monthlyNeededCents = Math.round(remaining / months);
	}
	return {
		id: g.id,
		name: g.name,
		targetCents: g.targetCents,
		targetDate: g.targetDate,
		accountId: g.accountId,
		accountName: account?.name ?? null,
		color: g.color,
		icon: g.icon,
		currentCents,
		percent: g.targetCents > 0 ? Math.min(999, (currentCents / g.targetCents) * 100) : 0,
		monthlyPaceCents,
		projectedDate,
		monthlyNeededCents,
		history,
		createdAt: g.createdAt
	};
}

function prefsFor(userId: string): Prefs {
	let p = prefs.get(userId);
	if (!p) {
		p = { remindersEnabled: true, daysBefore: [1], hourLocal: 9, activityEnabled: true };
		prefs.set(userId, p);
	}
	return p;
}
function prefsView(userId: string) {
	const p = prefsFor(userId);
	return {
		...p,
		subscriptions: [...pushSubs.values()]
			.filter((s) => s.userId === userId)
			.map((s) => ({
				id: s.id,
				userAgent: s.userAgent,
				createdAt: s.createdAt,
				lastUsedAt: s.lastUsedAt
			}))
	};
}

/** Scheduler step 1: mark autoPay occurrences due on `date` as paid (auto = true, paidBy null). */
function runAutopay(date: string) {
	let n = 0;
	for (const i of items.values()) {
		if (i.archived || !i.autoPay || i.kind === 'income') continue;
		if (!expand(i, date, date).includes(date)) continue;
		const key = `${i.id}:${date}`;
		if (payments.has(key)) continue;
		payments.set(key, {
			paidAt: nowIso(),
			paidBy: null,
			amountCents: i.amountCents,
			auto: true,
			source: 'autopay'
		});
		record(
			i.householdId,
			null,
			'occurrence.autopaid',
			'occurrence',
			i.id,
			`auto-paid ${i.name} · ${money(i.householdId, i.amountCents)}`,
			{
				itemId: i.id,
				dueDate: date,
				amountCents: i.amountCents
			}
		);
		n++;
	}
	return n;
}

// ---- v3: fake SimpleFIN Bridge, matcher, inbox ----------------------------------------------
const daysAgo = (n: number) => toIso(new Date(Date.now() - n * 86400_000));

/** The dataset `demo-token` claims: a checking account and a credit card at "Demo Bank". */
function demoBridge(): BridgeAccount[] {
	const first = `${todayIso().slice(0, 7)}-01`;
	return [
		{
			id: 'demo-checking',
			org: 'Demo Bank',
			name: 'Everyday Checking',
			currency: 'USD',
			balance: 234567,
			available: 234567,
			transactions: [
				{ id: 'tx-rent', posted: first, amount: -185000, description: 'RENT PAYMENT' },
				{ id: 'tx-kroger', posted: daysAgo(3), amount: -1249, description: 'KROGER #123' },
				{
					id: 'tx-payroll',
					posted: daysAgo(2),
					amount: 320000,
					description: 'ACME PAYROLL',
					payee: 'Acme Corp'
				},
				{ id: 'tx-coffee', posted: todayIso(), amount: -450, description: 'COFFEE', pending: true }
			]
		},
		{
			id: 'demo-visa',
			org: 'Demo Bank',
			name: 'Freedom Visa',
			currency: 'USD',
			balance: -54000,
			available: null,
			transactions: [
				{ id: 'tx-netflix', posted: daysAgo(5), amount: -1549, description: 'NETFLIX.COM' }
			]
		}
	];
}

/** payee_norm: lower-cased, digits/punctuation/noise removed. */
function normPayee(desc: string): string {
	return desc
		.toLowerCase()
		.replace(/\d+/g, ' ')
		.replace(/[^a-z\s]/g, ' ')
		.replace(
			/\b(pos|debit|credit|card|ach|chk|ck|web|online|purchase|pending|txn|ref|payment)\b/g,
			' '
		)
		.replace(/\s+/g, ' ')
		.trim();
}
const KEYWORDS: [RegExp, string][] = [
	[/kroger|safeway|aldi|walmart|trader joe|whole foods|costco|publix|wegmans|heb\b/i, 'Groceries'],
	[/netflix|spotify|hulu|disney|hbo|apple\.com|prime video|youtube/i, 'Subscriptions'],
	[/shell|chevron|exxon|\bbp\b|uber|lyft|transit|parking/i, 'Transportation'],
	[/electric|water|gas co|power|comcast|xfinity|verizon|at&t|t-mobile|internet/i, 'Utilities'],
	[/insurance|geico|allstate|state farm|progressive/i, 'Insurance'],
	[/cvs|walgreens|pharmacy|dental|clinic/i, 'Health'],
	[/restaurant|cafe|coffee|starbucks|mcdonald|chipotle|pizza|grill|bar\b/i, 'Fun'],
	[/payroll|direct dep|salary/i, 'Income']
];
function categoryByName(hh: string, name: string): Category | undefined {
	return [...categories.values()].find((c) => c.householdId === hh && c.name === name);
}
function fingerprintFor(
	source: 'simplefin' | 'csv',
	externalId: string | null,
	accountId: string | null,
	postedOn: string,
	amountCents: number,
	description: string
): string {
	return externalId
		? sha(`${source}|${externalId}`).toString('hex')
		: sha(`${accountId ?? ''}|${postedOn}|${amountCents}|${normPayee(description)}`).toString(
				'hex'
			);
}

/** Suggestion engine per PLAN-V3 (recurring → payee rule → keywords). */
function suggest(
	hh: string,
	tx: Pick<Tx, 'postedOn' | 'amountCents' | 'description' | 'payee'>
): {
	suggestion: Suggestion;
	confidence: number;
} {
	const abs = Math.abs(tx.amountCents);
	const outflow = tx.amountCents < 0;
	const norm = normPayee(tx.payee ?? tx.description);
	const rule = payeeRules.get(`${hh}:${norm}`);
	// 1. recurring occurrence within ±5 days, amount within max(2 %, $1), kind matches sign
	const from = toIso(new Date(parse(tx.postedOn).getTime() - 5 * 86400_000));
	const to = toIso(new Date(parse(tx.postedOn).getTime() + 5 * 86400_000));
	let best: { item: Item; date: string; distance: number } | null = null;
	for (const i of items.values()) {
		if (i.householdId !== hh || i.archived) continue;
		if ((i.kind === 'income') === outflow) continue;
		if (Math.abs(i.amountCents - abs) > Math.max(Math.round(i.amountCents * 0.02), 100)) continue;
		for (const d of expand(i, from, to)) {
			if (payments.has(`${i.id}:${d}`)) continue;
			const distance = Math.abs(parse(d).getTime() - parse(tx.postedOn).getTime());
			if (!best || distance < best.distance) best = { item: i, date: d, distance };
		}
	}
	if (best) {
		const confidence = 90 + (rule && rule.itemId === best.item.id ? 5 : 0);
		return {
			suggestion: {
				kind: 'occurrence',
				itemId: best.item.id,
				itemName: best.item.name,
				itemKind: best.item.kind,
				dueDate: best.date,
				confidence
			},
			confidence
		};
	}
	// 2. learned payee rule
	if (rule?.categoryId) {
		const c = categories.get(rule.categoryId);
		if (c) {
			const confidence = 70 + Math.min(rule.hits, 20);
			return {
				suggestion: { kind: 'category', categoryId: c.id, categoryName: c.name, confidence },
				confidence
			};
		}
	}
	// 3. keyword defaults
	for (const [re, name] of KEYWORDS) {
		if (!re.test(tx.description) && !(tx.payee && re.test(tx.payee))) continue;
		if ((name === 'Income') === outflow) continue;
		const c = categoryByName(hh, name);
		if (c)
			return {
				suggestion: { kind: 'category', categoryId: c.id, categoryName: c.name, confidence: 40 },
				confidence: 40
			};
	}
	return { suggestion: null, confidence: 0 };
}

/** Both sides of a transfer between two mapped accounts are suggested as ignore ("transfer"). */
function detectTransfers(hh: string, fresh: Tx[]) {
	const all = [...txs.values()].filter((t) => t.householdId === hh && t.status === 'pending');
	for (const a of fresh) {
		if (!a.accountId) continue;
		const b = all.find(
			(o) =>
				o.id !== a.id &&
				o.accountId &&
				o.accountId !== a.accountId &&
				o.amountCents === -a.amountCents &&
				Math.abs(parse(o.postedOn).getTime() - parse(a.postedOn).getTime()) <= 3 * 86400_000
		);
		if (!b) continue;
		for (const t of [a, b]) {
			t.suggestion = { kind: 'transfer', confidence: 80 };
			t.confidence = 80;
		}
	}
}

function insertTx(
	hh: string,
	input: Omit<
		Tx,
		| 'id'
		| 'householdId'
		| 'fingerprint'
		| 'status'
		| 'suggestion'
		| 'confidence'
		| 'confirmed'
		| 'createdAt'
	>
): Tx | null {
	const fingerprint = fingerprintFor(
		input.source,
		input.externalId,
		input.accountId,
		input.postedOn,
		input.amountCents,
		input.description
	);
	if ([...txs.values()].some((t) => t.householdId === hh && t.fingerprint === fingerprint))
		return null;
	const { suggestion, confidence } = suggest(hh, input);
	const tx: Tx = {
		...input,
		id: randomUUID(),
		householdId: hh,
		fingerprint,
		status: 'pending',
		suggestion,
		confidence,
		confirmed: null,
		createdAt: nowIso()
	};
	txs.set(tx.id, tx);
	return tx;
}

/** Mapping an external account pushes its balance into the CouplesCash account (+ snapshot). */
function applyMapping(ext: ExtAccount, account: Account | null, by: string) {
	ext.accountId = account?.id ?? null;
	ext.updatedAt = nowIso();
	for (const t of txs.values())
		if (t.externalAccountId === ext.id && t.status === 'pending') t.accountId = ext.accountId;
	if (!account) return;
	const owed = account.type === 'credit' || account.type === 'loan';
	const cents = owed ? Math.abs(ext.balanceCents) : ext.balanceCents;
	if (cents !== account.balanceCents) setBalance(account, cents, by);
}

type SyncResult = {
	linkId: string;
	newTransactions: number;
	updatedAccounts: number;
	autoMatched: number;
	errors: string[];
	syncedAt: string;
};
function syncLink(link: BankLinkRec, actor: User | null): SyncResult {
	const hh = link.householdId;
	const data = bridge.get(link.id) ?? [];
	const fresh: Tx[] = [];
	let updatedAccounts = 0;
	let autoMatched = 0;
	for (const ba of data) {
		let ext = [...extAccounts.values()].find(
			(x) => x.bankLinkId === link.id && x.externalId === ba.id
		);
		if (!ext) {
			ext = {
				id: randomUUID(),
				bankLinkId: link.id,
				householdId: hh,
				externalId: ba.id,
				orgName: ba.org,
				name: ba.name,
				currency: ba.currency,
				balanceCents: ba.balance,
				availableCents: ba.available,
				balanceAt: nowIso(),
				accountId: null,
				createdAt: nowIso(),
				updatedAt: nowIso()
			};
			extAccounts.set(ext.id, ext);
		} else {
			ext.balanceCents = ba.balance;
			ext.availableCents = ba.available;
			ext.balanceAt = nowIso();
			ext.updatedAt = nowIso();
		}
		updatedAccounts++;
		const account = ext.accountId ? accounts.get(ext.accountId) : undefined;
		if (account) applyMapping(ext, account, actor?.id ?? 'scheduler');
		for (const bt of ba.transactions) {
			const tx = insertTx(hh, {
				source: 'simplefin',
				externalAccountId: ext.id,
				accountId: ext.accountId,
				externalId: bt.id,
				postedOn: bt.posted,
				amountCents: bt.amount,
				description: bt.description,
				payee: bt.payee ?? null,
				memo: bt.memo ?? null,
				pending: !!bt.pending
			});
			if (tx) fresh.push(tx);
		}
	}
	detectTransfers(hh, fresh);
	// auto-mark: recurring match ≥ 90 whose item already has a learned payee rule
	for (const tx of fresh) {
		const s = tx.suggestion;
		if (!s || s.kind !== 'occurrence' || s.confidence < 90) continue;
		const norm = normPayee(tx.payee ?? tx.description);
		const rule = payeeRules.get(`${hh}:${norm}`);
		if (!rule || rule.itemId !== s.itemId) continue;
		const item = items.get(s.itemId);
		if (!item || payments.has(`${item.id}:${s.dueDate}`)) continue;
		payments.set(`${item.id}:${s.dueDate}`, {
			paidAt: nowIso(),
			paidBy: null,
			amountCents: Math.abs(tx.amountCents),
			auto: true,
			source: 'bank'
		});
		tx.status = 'confirmed';
		tx.confirmed = {
			expenseId: null,
			itemId: item.id,
			dueDate: s.dueDate,
			by: null,
			at: nowIso(),
			auto: true,
			categoryName: null,
			itemName: item.name
		};
		record(
			hh,
			null,
			'transaction.automatched',
			'transaction',
			tx.id,
			`matched ${item.name} · ${money(hh, Math.abs(tx.amountCents))} (auto)`,
			{ itemId: item.id, dueDate: s.dueDate }
		);
		autoMatched++;
	}
	link.lastSyncedAt = nowIso();
	link.lastError = null;
	record(
		hh,
		actor,
		'bank.synced',
		'bank_link',
		link.id,
		`synced ${link.name} · ${fresh.length} to review`,
		{ newTransactions: fresh.length, updatedAccounts, autoMatched }
	);
	return {
		linkId: link.id,
		newTransactions: fresh.length,
		updatedAccounts,
		autoMatched,
		errors: link.bridgeErrors,
		syncedAt: link.lastSyncedAt
	};
}

function extView(x: ExtAccount) {
	const a = x.accountId ? accounts.get(x.accountId) : undefined;
	return {
		id: x.id,
		externalId: x.externalId,
		orgName: x.orgName,
		name: x.name,
		currency: x.currency,
		balanceCents: x.balanceCents,
		availableCents: x.availableCents,
		balanceAt: x.balanceAt,
		accountId: a && !a.archived ? a.id : null,
		accountName: a && !a.archived ? a.name : null,
		updatedAt: x.updatedAt
	};
}
function linkView(l: BankLinkRec) {
	return {
		id: l.id,
		provider: l.provider,
		name: l.name,
		createdAt: l.createdAt,
		lastSyncedAt: l.lastSyncedAt,
		lastError: l.lastError,
		syncEnabled: l.syncEnabled,
		bridgeErrors: l.bridgeErrors,
		accounts: [...extAccounts.values()]
			.filter((x) => x.bankLinkId === l.id)
			.sort((a, b) => a.name.localeCompare(b.name))
			.map(extView)
	};
}
function txView(t: Tx) {
	const a = t.accountId ? accounts.get(t.accountId) : undefined;
	const by = t.confirmed?.by ? users.get(t.confirmed.by) : undefined;
	return {
		id: t.id,
		source: t.source,
		accountId: a?.id ?? null,
		accountName: a?.name ?? null,
		postedOn: t.postedOn,
		amountCents: t.amountCents,
		description: t.description,
		payee: t.payee,
		memo: t.memo,
		pending: t.pending,
		status: t.status,
		suggestion: t.suggestion,
		confirmed: t.confirmed ? { ...t.confirmed, byName: by?.displayName ?? null } : null,
		createdAt: t.createdAt
	};
}
function learnRule(
	hh: string,
	tx: Tx,
	patch: { categoryId?: string | null; itemId?: string | null }
) {
	const norm = normPayee(tx.payee ?? tx.description);
	if (!norm) return;
	const key = `${hh}:${norm}`;
	const rule = payeeRules.get(key) ?? {
		householdId: hh,
		payeeNorm: norm,
		categoryId: null,
		itemId: null,
		hits: 0
	};
	if ('categoryId' in patch) {
		rule.categoryId = patch.categoryId ?? null;
		rule.itemId = null;
	}
	if ('itemId' in patch) {
		rule.itemId = patch.itemId ?? null;
		rule.categoryId = null;
	}
	rule.hits++;
	payeeRules.set(key, rule);
}
type ConfirmError = { status: number; detail: string };
function confirmTx(
	hh: string,
	user: User,
	tx: Tx,
	body: Record<string, unknown>
): ConfirmError | null {
	if (tx.status !== 'pending') return { status: 400, detail: 'Not pending' };
	if (body.as === 'expense') {
		if (tx.amountCents >= 0) return { status: 400, detail: 'Money in cannot become an expense' };
		const categoryId = (body.categoryId as string | null) || null;
		const cat = categoryId ? categories.get(categoryId) : undefined;
		if (categoryId && (!cat || cat.householdId !== hh))
			return { status: 400, detail: 'Unknown category' };
		const e: Expense = {
			id: randomUUID(),
			householdId: hh,
			name: String(body.name ?? '') || tx.payee || tx.description,
			amountCents: Math.abs(tx.amountCents),
			categoryId: cat?.id ?? null,
			accountId: tx.accountId,
			spentOn: tx.postedOn,
			note: tx.memo,
			deducted: false,
			createdBy: user.id,
			createdAt: nowIso(),
			updatedAt: nowIso()
		};
		expenses.set(e.id, e);
		learnRule(hh, tx, { categoryId: cat?.id ?? null });
		tx.status = 'confirmed';
		tx.confirmed = {
			expenseId: e.id,
			itemId: null,
			dueDate: null,
			by: user.id,
			at: nowIso(),
			auto: false,
			categoryName: cat?.name ?? null,
			itemName: null
		};
		record(
			hh,
			user,
			'transaction.confirmed',
			'transaction',
			tx.id,
			`confirmed ${e.name} · ${money(hh, e.amountCents)}${cat ? ` in ${cat.name}` : ''}`,
			{ expenseId: e.id }
		);
		return null;
	}
	if (body.as === 'occurrence') {
		const item = items.get(String(body.itemId ?? ''));
		const date = String(body.dueDate ?? '');
		if (!item || item.householdId !== hh || item.archived)
			return { status: 400, detail: 'Unknown bill' };
		if ((item.kind === 'income') !== tx.amountCents > 0)
			return { status: 400, detail: 'That bill does not match the direction of this transaction' };
		if (!expand(item, date, date).includes(date))
			return { status: 400, detail: 'Not an occurrence' };
		if (payments.has(`${item.id}:${date}`))
			return { status: 400, detail: 'That occurrence is already paid' };
		payments.set(`${item.id}:${date}`, {
			paidAt: nowIso(),
			paidBy: user.id,
			amountCents: Math.abs(tx.amountCents),
			auto: false,
			source: 'bank'
		});
		learnRule(hh, tx, { itemId: item.id });
		tx.status = 'confirmed';
		tx.confirmed = {
			expenseId: null,
			itemId: item.id,
			dueDate: date,
			by: user.id,
			at: nowIso(),
			auto: false,
			categoryName: null,
			itemName: item.name
		};
		record(
			hh,
			user,
			'transaction.confirmed',
			'transaction',
			tx.id,
			`matched ${item.name} · ${money(hh, Math.abs(tx.amountCents))}`,
			{ itemId: item.id, dueDate: date }
		);
		return null;
	}
	return { status: 400, detail: 'Unknown confirm mode' };
}
function restoreTx(hh: string, user: User | null, tx: Tx) {
	const c = tx.confirmed;
	if (c?.expenseId) expenses.delete(c.expenseId); // deducted = false, so no balance to put back
	if (c?.itemId && c.dueDate) payments.delete(`${c.itemId}:${c.dueDate}`);
	tx.status = 'pending';
	tx.confirmed = null;
	record(
		hh,
		user,
		'transaction.restored',
		'transaction',
		tx.id,
		`put ${tx.payee ?? tx.description} back in the inbox`
	);
}
function restoreTxForOccurrence(hh: string, itemId: string, date: string) {
	for (const t of txs.values()) {
		if (t.householdId !== hh || t.status !== 'confirmed') continue;
		if (t.confirmed?.itemId === itemId && t.confirmed.dueDate === date) {
			t.status = 'pending';
			t.confirmed = null;
		}
	}
}
function inboxSummary(hh: string) {
	const mine = [...txs.values()].filter((t) => t.householdId === hh);
	const links = [...bankLinks.values()].filter((l) => l.householdId === hh);
	return {
		pending: mine.filter((t) => t.status === 'pending').length,
		suggested: mine.filter(
			(t) => t.status === 'pending' && t.confidence >= 70 && t.suggestion?.kind !== 'transfer'
		).length,
		lastSyncedAt: links.reduce<string | null>(
			(acc, l) => (l.lastSyncedAt && (!acc || l.lastSyncedAt > acc) ? l.lastSyncedAt : acc),
			null
		),
		linkCount: links.length
	};
}

// ---- http -------------------------------------------------------------------------------
function send(res: ServerResponse, status: number, body?: unknown) {
	if (body === undefined) {
		res.writeHead(status);
		res.end();
		return;
	}
	res.writeHead(status, {
		'content-type': status >= 400 ? 'application/problem+json' : 'application/json'
	});
	res.end(JSON.stringify(body));
}
const problem = (
	res: ServerResponse,
	status: number,
	title: string,
	detail?: string,
	type?: string
) => send(res, status, { type: type ?? 'about:blank', title, status, detail });

async function readJson(req: IncomingMessage): Promise<Record<string, unknown>> {
	const chunks: Buffer[] = [];
	for await (const c of req) chunks.push(c as Buffer);
	const text = Buffer.concat(chunks).toString('utf8');
	return text ? (JSON.parse(text) as Record<string, unknown>) : {};
}

function auth(req: IncomingMessage): User | null {
	const h = req.headers.authorization;
	if (!h?.startsWith('Bearer ')) return null;
	try {
		const payload = JSON.parse(Buffer.from(h.slice(7).split('.')[1], 'base64url').toString()) as {
			sub: string;
			exp: number;
		};
		if (payload.exp * 1000 < Date.now()) return null;
		return users.get(payload.sub) ?? null;
	} catch {
		return null;
	}
}

const server = createServer(async (req, res) => {
	const url = new URL(req.url ?? '/', 'http://localhost');
	const p = url.pathname.replace(/\/+$/, '');
	const m = req.method ?? 'GET';
	const log = process.env.MOCK_API_LOG ? console.log : () => {};
	log(m, p);

	try {
		if (p === '/health') return send(res, 200, { status: 'ok', db: 'ok' });
		if (p === '/__reset' && m === 'POST') {
			for (const map of [
				users,
				households,
				accounts,
				snapshots,
				categories,
				items,
				payments,
				refreshTokens,
				expenses,
				budgets,
				todos,
				goals,
				pushSubs,
				prefs,
				bankLinks,
				extAccounts,
				txs,
				payeeRules,
				csvImports,
				bridge
			])
				map.clear();
			codes.length = 0;
			activity.length = 0;
			return send(res, 204);
		}
		if (p === '/__mock/bank-sync' && m === 'POST') {
			let synced = 0;
			for (const link of bankLinks.values()) {
				if (!link.syncEnabled) continue;
				syncLink(link, null);
				synced++;
			}
			return send(res, 200, { synced });
		}
		if (p === '/__mock/autopay' && m === 'POST') {
			const date = url.searchParams.get('date') ?? todayIso();
			return send(res, 200, { date, marked: runAutopay(date) });
		}

		// --- anonymous passkey routes
		if (p === '/v1/auth/passkeys/config' && m === 'GET')
			return send(res, 200, { enabled: false, rpId: 'localhost' });
		if (p === '/v1/auth/passkeys/login/options' && m === 'POST')
			return problem(res, 503, 'Passkeys not configured');
		if (p === '/v1/auth/passkeys/login' && m === 'POST')
			return problem(res, 503, 'Passkeys not configured');

		// --- auth
		if (p === '/v1/auth/register' && m === 'POST') {
			const b = await readJson(req);
			const email = String(b.email ?? '').toLowerCase();
			if (!email.includes('@') || String(b.password ?? '').length < 10 || !b.displayName)
				return problem(res, 400, 'Validation failed');
			if ([...users.values()].some((u) => u.email === email))
				return problem(res, 409, 'Email already registered');
			const u: User = {
				id: randomUUID(),
				email,
				displayName: String(b.displayName),
				password: String(b.password),
				createdAt: nowIso(),
				householdId: null
			};
			users.set(u.id, u);
			return send(res, 201, authResult(u));
		}
		if (p === '/v1/auth/login' && m === 'POST') {
			const b = await readJson(req);
			const u = [...users.values()].find((x) => x.email === String(b.email ?? '').toLowerCase());
			if (!u || u.password !== b.password) return problem(res, 401, 'Invalid email or password');
			return send(res, 200, authResult(u));
		}
		if (p === '/v1/auth/refresh' && m === 'POST') {
			const b = await readJson(req);
			const rec = refreshTokens.get(String(b.refreshToken ?? ''));
			if (!rec || rec.revoked) {
				if (rec)
					for (const r of refreshTokens.values()) if (r.family === rec.family) r.revoked = true;
				return problem(res, 401, 'Invalid refresh token');
			}
			rec.revoked = true;
			const u = users.get(rec.userId)!;
			const result = authResult(u);
			refreshTokens.get(result.refreshToken)!.family = rec.family;
			return send(res, 200, result);
		}
		if (p === '/v1/auth/logout' && m === 'POST') {
			const b = await readJson(req);
			const rec = refreshTokens.get(String(b.refreshToken ?? ''));
			if (rec) rec.revoked = true;
			return send(res, 204);
		}

		const user = auth(req);
		if (!user) return problem(res, 401, 'Unauthorized');

		if (p === '/v1/auth/me' && m === 'GET')
			return send(res, 200, {
				user: userView(user),
				household: householdView(user.householdId ? households.get(user.householdId)! : null)
			});
		if (p === '/v1/auth/me' && m === 'PATCH') {
			const b = await readJson(req);
			if (typeof b.displayName === 'string' && b.displayName.trim()) {
				user.displayName = b.displayName.trim();
				if (user.householdId)
					record(
						user.householdId,
						user,
						'user.renamed',
						'user',
						user.id,
						`is now called ${user.displayName}`
					);
			}
			return send(res, 200, userView(user));
		}
		if (p === '/v1/auth/password' && m === 'POST') {
			const b = await readJson(req);
			if (b.currentPassword !== user.password) return problem(res, 401, 'Wrong password');
			if (String(b.newPassword ?? '').length < 10) return problem(res, 400, 'Validation failed');
			user.password = String(b.newPassword);
			for (const r of refreshTokens.values()) if (r.userId === user.id) r.revoked = true;
			return send(res, 204);
		}

		// --- passkeys (authenticated; disabled in the mock)
		if (p === '/v1/auth/passkeys' && m === 'GET') return send(res, 200, []);
		if (p.startsWith('/v1/auth/passkeys')) return problem(res, 503, 'Passkeys not configured');

		// --- push (per user)
		if (p === '/v1/push/vapid-public-key' && m === 'GET') {
			if (!PUSH_ENABLED)
				return problem(
					res,
					503,
					'Push not configured',
					undefined,
					'https://couplescash.app/problems/push_not_configured'
				);
			return send(res, 200, { publicKey: 'BMockVapidPublicKey_' + 'A'.repeat(60) });
		}
		if (p === '/v1/push/subscriptions' && m === 'POST') {
			const b = await readJson(req);
			const endpoint = String(b.endpoint ?? '');
			if (!endpoint) return problem(res, 400, 'Validation failed');
			const keys = (b.keys ?? {}) as { p256dh?: string; auth?: string };
			const existing = pushSubs.get(endpoint);
			const sub: PushSub = existing ?? {
				id: randomUUID(),
				userId: user.id,
				endpoint,
				p256dh: keys.p256dh ?? '',
				auth: keys.auth ?? '',
				userAgent: String(b.userAgent ?? ''),
				createdAt: nowIso(),
				lastUsedAt: null
			};
			sub.userId = user.id;
			pushSubs.set(endpoint, sub);
			return send(res, 201, { id: sub.id });
		}
		if (p === '/v1/push/subscriptions' && m === 'DELETE') {
			const b = await readJson(req);
			for (const [ep, s] of pushSubs) {
				if (s.userId !== user.id) continue;
				if (ep === b.endpoint || s.id === b.id) pushSubs.delete(ep);
			}
			return send(res, 204);
		}
		if (p === '/v1/push/prefs' && m === 'GET') return send(res, 200, prefsView(user.id));
		if (p === '/v1/push/prefs' && m === 'PUT') {
			const b = await readJson(req);
			prefs.set(user.id, {
				remindersEnabled: Boolean(b.remindersEnabled),
				daysBefore: Array.isArray(b.daysBefore) ? (b.daysBefore as number[]).slice(0, 5) : [1],
				hourLocal: Number(b.hourLocal ?? 9),
				activityEnabled: Boolean(b.activityEnabled)
			});
			return send(res, 200, prefsView(user.id));
		}
		if (p === '/v1/push/test' && m === 'POST') return send(res, 202);

		// --- couple
		if (p === '/v1/couple' && m === 'GET')
			return send(
				res,
				200,
				householdView(user.householdId ? households.get(user.householdId)! : null)
			);
		if (p === '/v1/couple' && m === 'PATCH') {
			const h = user.householdId ? households.get(user.householdId) : null;
			if (!h) return problem(res, 409, 'No household');
			const b = await readJson(req);
			const changed: string[] = [];
			if (typeof b.name === 'string' && b.name.trim()) {
				h.name = b.name.trim();
				changed.push('name');
			}
			if (typeof b.currency === 'string') {
				if (!/^[A-Z]{3}$/.test(b.currency))
					return send(res, 400, {
						title: 'Validation failed',
						status: 400,
						errors: { currency: ['Must be a 3-letter ISO code'] }
					});
				h.currency = b.currency;
				changed.push('currency');
			}
			if (b.weekStart === 0 || b.weekStart === 1) {
				h.weekStart = b.weekStart;
				changed.push('week start');
			}
			if (typeof b.timezone === 'string' && b.timezone) {
				try {
					new Intl.DateTimeFormat('en-US', { timeZone: b.timezone });
				} catch {
					return send(res, 400, {
						title: 'Validation failed',
						status: 400,
						errors: { timezone: ['Unknown time zone'] }
					});
				}
				h.timezone = b.timezone;
				changed.push('time zone');
			}
			if ('defaultAccountId' in b) {
				const id = (b.defaultAccountId as string | null) || null;
				if (id) {
					const a = accounts.get(id);
					if (!a || a.householdId !== h.id)
						return send(res, 400, {
							title: 'Validation failed',
							status: 400,
							errors: { defaultAccountId: ['Unknown account'] }
						});
				}
				h.defaultAccountId = id;
				changed.push('default account');
			}
			if (typeof b.payPeriod === 'string') {
				if (!['weekly', 'biweekly', 'bimonthly', 'monthly'].includes(b.payPeriod))
					return send(res, 400, {
						title: 'Validation failed',
						status: 400,
						errors: { payPeriod: ['Unknown pay period'] }
					});
				h.payPeriod = b.payPeriod as Household['payPeriod'];
				changed.push('pay period');
			}
			if ('payAnchorDate' in b) {
				h.payAnchorDate = (b.payAnchorDate as string | null) || null;
			}
			if (changed.length)
				record(
					h.id,
					user,
					'couple.settings',
					'couple',
					h.id,
					`updated household ${changed.join(', ')}`
				);
			return send(res, 200, householdView(h));
		}
		if (p === '/v1/couple/code' && m === 'POST') {
			const b = await readJson(req);
			let h = user.householdId ? households.get(user.householdId)! : null;
			if (!h) h = createHousehold(String(b.name ?? ''), user);
			if (h.memberIds.length >= 2) return problem(res, 409, 'Household is full');
			for (const c of codes) if (c.householdId === h.id && !c.redeemed) c.redeemed = true;
			const bytes = randomBytes(20);
			const payload = `cc1.${bytes.toString('base64url')}`;
			const alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
			const digest = sha(bytes);
			let bits = 0n;
			for (let i = 0; i < 5; i++) bits = (bits << 8n) | BigInt(digest[i]);
			let raw = '';
			for (let i = 7; i >= 0; i--) raw = alphabet[Number((bits >> BigInt(i * 5)) & 31n)] + raw;
			const code = `${raw.slice(0, 4)}-${raw.slice(4)}`;
			const expiresAt = Date.now() + 10 * 60_000;
			codes.push({
				bytesHash: sha(bytes).toString('hex'),
				codeHash: sha(raw).toString('hex'),
				code,
				payload,
				householdId: h.id,
				expiresAt,
				redeemed: false
			});
			return send(res, 201, {
				code,
				payload,
				expiresAt: new Date(expiresAt).toISOString(),
				household: householdView(h)
			});
		}
		if (p === '/v1/couple/join' && m === 'POST') {
			if (user.householdId) return problem(res, 409, 'Already in a household');
			const b = await readJson(req);
			const input = String(b.code ?? '').trim();
			let found: Code | undefined;
			if (input.startsWith('cc1.')) {
				const hash = sha(Buffer.from(input.slice(4), 'base64url')).toString('hex');
				found = codes.find((c) => c.bytesHash === hash);
			} else {
				const hash = sha(input.replace(/[\s-]/g, '').toUpperCase()).toString('hex');
				found = codes.find((c) => c.codeHash === hash);
			}
			if (!found || found.redeemed || found.expiresAt < Date.now())
				return problem(res, 404, 'Invalid or expired code');
			const h = households.get(found.householdId)!;
			if (h.memberIds.length >= 2) return problem(res, 404, 'Invalid or expired code');
			found.redeemed = true;
			h.memberIds.push(user.id);
			h.joinedAt[user.id] = nowIso();
			user.householdId = h.id;
			record(h.id, user, 'couple.joined', 'couple', h.id, 'joined the household');
			return send(res, 200, authResult(user));
		}
		if (p === '/v1/couple/leave' && m === 'POST') {
			const h = user.householdId ? households.get(user.householdId) : null;
			if (h) {
				h.memberIds = h.memberIds.filter((id) => id !== user.id);
				if (h.memberIds.length === 0) households.delete(h.id);
				else record(h.id, user, 'couple.left', 'couple', h.id, 'left the household');
			}
			user.householdId = null;
			return send(res, 200, authResult(user));
		}

		const hh = user.householdId;
		if (!hh) return problem(res, 409, 'No household');
		const $ = (cents: number) => money(hh, cents);

		// --- accounts
		if (p === '/v1/accounts' && m === 'GET') {
			const order = ['checking', 'savings', 'cash', 'investment', 'credit', 'loan'];
			const list = [...accounts.values()]
				.filter((a) => a.householdId === hh && !a.archived)
				.sort(
					(a, b) => order.indexOf(a.type) - order.indexOf(b.type) || a.name.localeCompare(b.name)
				);
			return send(res, 200, list.map(accountView));
		}
		if (p === '/v1/accounts' && m === 'POST') {
			const b = await readJson(req);
			if (!b.name || !b.institution || !b.type || typeof b.balanceCents !== 'number')
				return problem(res, 400, 'Validation failed');
			const a: Account = {
				id: randomUUID(),
				householdId: hh,
				name: String(b.name),
				institution: String(b.institution),
				type: String(b.type),
				balanceCents: b.balanceCents,
				color: String(b.color ?? '#2a78d6'),
				icon: String(b.icon ?? 'landmark'),
				note: (b.note as string | null) ?? null,
				createdAt: nowIso(),
				updatedAt: nowIso(),
				archived: false
			};
			accounts.set(a.id, a);
			snapshots.set(a.id, [
				{ balanceCents: a.balanceCents, recordedAt: a.createdAt, recordedBy: user.id }
			]);
			record(
				hh,
				user,
				'account.created',
				'account',
				a.id,
				`added account ${a.name} · ${$(a.balanceCents)}`
			);
			return send(res, 201, accountView(a));
		}
		let mm = /^\/v1\/accounts\/([^/]+)(\/balance|\/history)?$/.exec(p);
		if (mm) {
			const a = accounts.get(mm[1]);
			if (!a || a.householdId !== hh || a.archived) return problem(res, 404, 'Not found');
			if (mm[2] === '/history' && m === 'GET')
				return send(
					res,
					200,
					(snapshots.get(a.id) ?? []).slice(0, Number(url.searchParams.get('limit') ?? 30))
				);
			if (mm[2] === '/balance' && m === 'POST') {
				const b = await readJson(req);
				if (typeof b.balanceCents !== 'number') return problem(res, 400, 'Validation failed');
				setBalance(a, b.balanceCents, user.id);
				record(
					hh,
					user,
					'account.balance',
					'account',
					a.id,
					`updated ${a.name} balance to ${$(a.balanceCents)}`
				);
				return send(res, 200, accountView(a));
			}
			if (!mm[2] && m === 'GET') return send(res, 200, accountView(a));
			if (!mm[2] && m === 'PUT') {
				const b = await readJson(req);
				const balanceChanged =
					typeof b.balanceCents === 'number' && b.balanceCents !== a.balanceCents;
				Object.assign(a, {
					name: b.name,
					institution: b.institution,
					type: b.type,
					color: b.color ?? a.color,
					icon: b.icon ?? a.icon,
					note: b.note ?? null,
					updatedAt: nowIso()
				});
				if (balanceChanged) setBalance(a, b.balanceCents as number, user.id);
				record(hh, user, 'account.updated', 'account', a.id, `updated account ${a.name}`);
				return send(res, 200, accountView(a));
			}
			if (!mm[2] && m === 'DELETE') {
				a.archived = true;
				record(hh, user, 'account.deleted', 'account', null, `removed account ${a.name}`);
				return send(res, 204);
			}
		}

		// --- categories
		if (p === '/v1/categories' && m === 'GET')
			return send(
				res,
				200,
				[...categories.values()]
					.filter((c) => c.householdId === hh)
					.sort((a, b) => a.sortOrder - b.sortOrder)
					.map(categoryView)
			);
		if (p === '/v1/categories' && m === 'POST') {
			const b = await readJson(req);
			const c: Category = {
				id: randomUUID(),
				householdId: hh,
				name: String(b.name),
				color: String(b.color),
				icon: String(b.icon),
				sortOrder: 99,
				isDefault: false
			};
			categories.set(c.id, c);
			record(hh, user, 'category.created', 'category', c.id, `added category ${c.name}`);
			return send(res, 201, categoryView(c));
		}
		mm = /^\/v1\/categories\/([^/]+)$/.exec(p);
		if (mm) {
			const c = categories.get(mm[1]);
			if (!c || c.householdId !== hh) return problem(res, 404, 'Not found');
			if (m === 'PUT') {
				const b = await readJson(req);
				Object.assign(c, {
					name: b.name,
					color: b.color,
					icon: b.icon,
					sortOrder: b.sortOrder ?? c.sortOrder
				});
				return send(res, 200, categoryView(c));
			}
			if (m === 'DELETE') {
				categories.delete(c.id);
				for (const i of items.values()) if (i.categoryId === c.id) i.categoryId = null;
				for (const e of expenses.values()) if (e.categoryId === c.id) e.categoryId = null;
				budgets.delete(`${hh}:${c.id}`);
				return send(res, 204);
			}
		}

		// --- recurring
		if (p === '/v1/recurring' && m === 'GET') {
			const kind = url.searchParams.get('kind');
			return send(
				res,
				200,
				[...items.values()]
					.filter((i) => i.householdId === hh && !i.archived && (!kind || i.kind === kind))
					.map(itemView)
			);
		}
		if (p === '/v1/recurring' && m === 'POST') {
			const b = await readJson(req);
			if (!b.name || !b.kind || typeof b.amountCents !== 'number' || !b.frequency || !b.anchorDate)
				return problem(res, 400, 'Validation failed');
			const i: Item = {
				id: randomUUID(),
				householdId: hh,
				name: String(b.name),
				kind: String(b.kind),
				amountCents: b.amountCents,
				categoryId: (b.categoryId as string | null) || null,
				accountId: (b.accountId as string | null) || null,
				frequency: String(b.frequency),
				anchorDate: String(b.anchorDate),
				endDate: (b.endDate as string | null) || null,
				autoPay: Boolean(b.autoPay),
				notes: (b.notes as string | null) ?? null,
				url: (b.url as string | null) ?? null,
				createdAt: nowIso(),
				updatedAt: nowIso(),
				archived: false
			};
			items.set(i.id, i);
			record(
				hh,
				user,
				'recurring.created',
				'recurring',
				i.id,
				`added ${i.kind} ${i.name} · ${$(i.amountCents)}`
			);
			return send(res, 201, itemView(i));
		}
		mm = /^\/v1\/recurring\/([^/]+)(?:\/occurrences\/(\d{4}-\d{2}-\d{2})\/paid)?$/.exec(p);
		if (mm) {
			const i = items.get(mm[1]);
			if (!i || i.householdId !== hh || i.archived) return problem(res, 404, 'Not found');
			if (mm[2]) {
				const date = mm[2];
				if (!expand(i, date, date).includes(date)) return problem(res, 404, 'Not an occurrence');
				if (m === 'PUT') {
					const b = await readJson(req);
					const amount = typeof b.amountCents === 'number' ? b.amountCents : i.amountCents;
					payments.set(`${i.id}:${date}`, {
						paidAt: nowIso(),
						paidBy: user.id,
						amountCents: amount,
						auto: false,
						source: 'manual'
					});
					record(
						hh,
						user,
						'occurrence.paid',
						'occurrence',
						i.id,
						`marked ${i.name} paid · ${$(amount)}`,
						{ itemId: i.id, dueDate: date }
					);
					return send(res, 200, occurrence(i, date));
				}
				if (m === 'DELETE') {
					const prev = payments.get(`${i.id}:${date}`);
					payments.delete(`${i.id}:${date}`);
					// v3: unpaying a payment that came from the inbox puts the row back in review
					if (prev?.source === 'bank') restoreTxForOccurrence(hh, i.id, date);
					record(hh, user, 'occurrence.unpaid', 'occurrence', i.id, `marked ${i.name} unpaid`, {
						itemId: i.id,
						dueDate: date
					});
					return send(res, 200, occurrence(i, date));
				}
			} else {
				if (m === 'GET') return send(res, 200, itemView(i));
				if (m === 'PUT') {
					const b = await readJson(req);
					Object.assign(i, {
						name: b.name,
						kind: b.kind,
						amountCents: b.amountCents,
						categoryId: (b.categoryId as string | null) || null,
						accountId: (b.accountId as string | null) || null,
						frequency: b.frequency,
						anchorDate: b.anchorDate,
						endDate: (b.endDate as string | null) || null,
						autoPay: Boolean(b.autoPay),
						notes: b.notes ?? null,
						url: b.url ?? null,
						updatedAt: nowIso()
					});
					record(hh, user, 'recurring.updated', 'recurring', i.id, `updated ${i.name}`);
					return send(res, 200, itemView(i));
				}
				if (m === 'DELETE') {
					i.archived = true;
					record(hh, user, 'recurring.deleted', 'recurring', null, `removed ${i.name}`);
					return send(res, 204);
				}
			}
		}

		// --- calendar & summary
		if (p === '/v1/calendar' && m === 'GET') {
			const from = url.searchParams.get('from') ?? todayIso();
			const to = url.searchParams.get('to') ?? from;
			return send(res, 200, { from, to, occurrences: calendar(hh, from, to) });
		}
		if (p === '/v1/summary' && m === 'GET') {
			const month = url.searchParams.get('month') ?? todayIso().slice(0, 7);
			return send(res, 200, summaryFor(hh, month));
		}

		// --- activity
		if (p === '/v1/activity' && m === 'GET') {
			const before = url.searchParams.get('before');
			const limit = Math.min(100, Number(url.searchParams.get('limit') ?? 30));
			const all = activity
				.filter((a) => a.householdId === hh && (!before || a.createdAt < before))
				.sort((a, b) => b.createdAt.localeCompare(a.createdAt));
			const page = all.slice(0, limit);
			return send(res, 200, {
				events: page.map(activityView),
				nextBefore: all.length > limit ? page[page.length - 1].createdAt : null
			});
		}

		// --- expenses
		if (p === '/v1/expenses' && m === 'GET') {
			let from = url.searchParams.get('from');
			let to = url.searchParams.get('to');
			if (!from || !to) {
				[from, to] = monthRange(url.searchParams.get('month') ?? todayIso().slice(0, 7));
			}
			return send(res, 200, hhExpenses(hh, from, to).map(expenseView));
		}
		if (p === '/v1/expenses' && m === 'POST') {
			const b = await readJson(req);
			if (!b.name || typeof b.amountCents !== 'number' || b.amountCents <= 0 || !b.spentOn)
				return problem(res, 400, 'Validation failed');
			const accountId = (b.accountId as string | null) || null;
			const account = accountId ? accounts.get(accountId) : undefined;
			if (accountId && (!account || account.householdId !== hh))
				return send(res, 400, {
					title: 'Validation failed',
					status: 400,
					errors: { accountId: ['Unknown account'] }
				});
			const deduct = account ? (b.deduct === undefined ? true : Boolean(b.deduct)) : false;
			const e: Expense = {
				id: randomUUID(),
				householdId: hh,
				name: String(b.name),
				amountCents: b.amountCents,
				categoryId: (b.categoryId as string | null) || null,
				accountId,
				spentOn: String(b.spentOn),
				note: (b.note as string | null) ?? null,
				deducted: deduct,
				createdBy: user.id,
				createdAt: nowIso(),
				updatedAt: nowIso()
			};
			if (deduct && account) setBalance(account, account.balanceCents - e.amountCents, user.id);
			expenses.set(e.id, e);
			record(
				hh,
				user,
				'expense.created',
				'expense',
				e.id,
				`spent ${$(e.amountCents)} on ${e.name}`,
				{ expenseId: e.id }
			);
			return send(res, 201, expenseView(e));
		}
		mm = /^\/v1\/expenses\/([^/]+)$/.exec(p);
		if (mm) {
			const e = expenses.get(mm[1]);
			if (!e || e.householdId !== hh) return problem(res, 404, 'Not found');
			if (m === 'GET') return send(res, 200, expenseView(e));
			if (m === 'PUT') {
				const b = await readJson(req);
				if (!b.name || typeof b.amountCents !== 'number' || b.amountCents <= 0 || !b.spentOn)
					return problem(res, 400, 'Validation failed');
				// reverse the old deduction, apply the new one
				if (e.deducted && e.accountId) {
					const old = accounts.get(e.accountId);
					if (old) setBalance(old, old.balanceCents + e.amountCents, user.id);
				}
				const accountId = (b.accountId as string | null) || null;
				const account = accountId ? accounts.get(accountId) : undefined;
				const deduct = account ? (b.deduct === undefined ? true : Boolean(b.deduct)) : false;
				Object.assign(e, {
					name: String(b.name),
					amountCents: b.amountCents,
					categoryId: (b.categoryId as string | null) || null,
					accountId,
					spentOn: String(b.spentOn),
					note: (b.note as string | null) ?? null,
					deducted: deduct,
					updatedAt: nowIso()
				});
				if (deduct && account) setBalance(account, account.balanceCents - e.amountCents, user.id);
				record(
					hh,
					user,
					'expense.updated',
					'expense',
					e.id,
					`updated expense ${e.name} · ${$(e.amountCents)}`
				);
				return send(res, 200, expenseView(e));
			}
			if (m === 'DELETE') {
				if (e.deducted && e.accountId) {
					const a = accounts.get(e.accountId);
					if (a) setBalance(a, a.balanceCents + e.amountCents, user.id);
				}
				expenses.delete(e.id);
				record(hh, user, 'expense.deleted', 'expense', null, `deleted expense ${e.name}`);
				return send(res, 204);
			}
		}

		// --- budgets
		if (p === '/v1/budgets' && m === 'GET') {
			const month = url.searchParams.get('month') ?? todayIso().slice(0, 7);
			return send(res, 200, budgetRows(hh, month));
		}
		mm = /^\/v1\/budgets\/([^/]+)$/.exec(p);
		if (mm) {
			const c = categories.get(mm[1]);
			if (!c || c.householdId !== hh) return problem(res, 404, 'Not found');
			const month = url.searchParams.get('month') ?? todayIso().slice(0, 7);
			if (m === 'PUT') {
				const b = await readJson(req);
				if (typeof b.amountCents !== 'number' || b.amountCents < 0)
					return problem(res, 400, 'Validation failed');
				budgets.set(`${hh}:${c.id}`, b.amountCents);
				record(
					hh,
					user,
					'budget.set',
					'budget',
					c.id,
					`set the ${c.name} budget to ${$(b.amountCents)}`
				);
				const [first, last] = monthRange(month);
				const active = [...items.values()].filter((i) => i.householdId === hh && !i.archived);
				return send(res, 200, budgetRow(hh, c, active, hhExpenses(hh, first, last)));
			}
			if (m === 'DELETE') {
				budgets.delete(`${hh}:${c.id}`);
				record(hh, user, 'budget.cleared', 'budget', c.id, `cleared the ${c.name} budget`);
				return send(res, 204);
			}
		}

		// --- pay period
		if (p === '/v1/payperiod' && m === 'GET') {
			const h = households.get(hh)!;
			if (!h.payAnchorDate) return problem(res, 409, 'Pay period not configured');
			const date = url.searchParams.get('date') ?? todayIso();
			const bounds = periodFor(h.payPeriod, h.payAnchorDate, date);
			const occ = calendar(hh, bounds.start, bounds.end);
			const out = occ.filter((o) => o.kind !== 'income');
			const dueCents = out.reduce((s, o) => s + o.amountCents, 0);
			const paidCents = out
				.filter((o) => o.paid)
				.reduce((s, o) => s + (o.paid?.amountCents ?? 0), 0);
			const remainingCents = out.filter((o) => !o.paid).reduce((s, o) => s + o.amountCents, 0);
			const liquid = [...accounts.values()]
				.filter(
					(a) => a.householdId === hh && !a.archived && (a.type === 'checking' || a.type === 'cash')
				)
				.reduce((s, a) => s + a.balanceCents, 0);
			return send(res, 200, {
				period: h.payPeriod,
				start: bounds.start,
				end: bounds.end,
				nextPayday: bounds.nextPayday,
				daysLeft: bounds.daysLeft,
				expectedIncomeCents: occ
					.filter((o) => o.kind === 'income')
					.reduce((s, o) => s + o.amountCents, 0),
				dueCents,
				paidCents,
				remainingCents,
				spendableCents: liquid - remainingCents,
				occurrences: occ,
				expensesCents: hhExpenses(hh, bounds.start, bounds.end).reduce(
					(s, e) => s + e.amountCents,
					0
				)
			});
		}

		// --- todos
		const hhTodos = () => [...todos.values()].filter((t) => t.householdId === hh);
		if (p === '/v1/todos' && m === 'GET') {
			const includeDone = url.searchParams.get('includeDone') === 'true';
			const cutoff = new Date(Date.now() - 30 * 86400_000).toISOString();
			const open = hhTodos()
				.filter((t) => !t.doneAt)
				.sort((a, b) => a.sortOrder - b.sortOrder);
			const done = includeDone
				? hhTodos()
						.filter((t) => t.doneAt && t.doneAt >= cutoff)
						.sort((a, b) => b.doneAt!.localeCompare(a.doneAt!))
				: [];
			return send(res, 200, [...open, ...done].map(todoView));
		}
		if (p === '/v1/todos' && m === 'POST') {
			const b = await readJson(req);
			const title = String(b.title ?? '').trim();
			if (!title || title.length > 120) return problem(res, 400, 'Validation failed');
			const t: Todo = {
				id: randomUUID(),
				householdId: hh,
				title,
				amountCents: typeof b.amountCents === 'number' ? b.amountCents : null,
				dueDate: (b.dueDate as string | null) || null,
				sortOrder: hhTodos().length,
				doneAt: null,
				doneBy: null,
				createdBy: user.id,
				createdAt: nowIso()
			};
			todos.set(t.id, t);
			record(hh, user, 'todo.created', 'todo', t.id, `added to-pay ${t.title}`);
			return send(res, 201, todoView(t));
		}
		if (p === '/v1/todos/order' && m === 'PUT') {
			const b = await readJson(req);
			const ids = Array.isArray(b.ids) ? (b.ids as string[]) : [];
			ids.forEach((id, i) => {
				const t = todos.get(id);
				if (t && t.householdId === hh) t.sortOrder = i;
			});
			return send(res, 204);
		}
		mm = /^\/v1\/todos\/([^/]+)(\/done)?$/.exec(p);
		if (mm) {
			const t = todos.get(mm[1]);
			if (!t || t.householdId !== hh) return problem(res, 404, 'Not found');
			if (mm[2] && m === 'POST') {
				t.doneAt = nowIso();
				t.doneBy = user.id;
				record(hh, user, 'todo.done', 'todo', t.id, `ticked off ${t.title}`);
				return send(res, 200, todoView(t));
			}
			if (mm[2] && m === 'DELETE') {
				t.doneAt = null;
				t.doneBy = null;
				record(hh, user, 'todo.undone', 'todo', t.id, `reopened ${t.title}`);
				return send(res, 200, todoView(t));
			}
			if (!mm[2] && m === 'PUT') {
				const b = await readJson(req);
				Object.assign(t, {
					title: String(b.title ?? t.title),
					amountCents: typeof b.amountCents === 'number' ? b.amountCents : null,
					dueDate: (b.dueDate as string | null) || null,
					sortOrder: typeof b.sortOrder === 'number' ? b.sortOrder : t.sortOrder
				});
				return send(res, 200, todoView(t));
			}
			if (!mm[2] && m === 'DELETE') {
				todos.delete(t.id);
				record(hh, user, 'todo.deleted', 'todo', null, `deleted to-pay ${t.title}`);
				return send(res, 204);
			}
		}

		// --- goals
		if (p === '/v1/goals' && m === 'GET')
			return send(
				res,
				200,
				[...goals.values()].filter((g) => g.householdId === hh && !g.archived).map(goalView)
			);
		if (p === '/v1/goals' && m === 'POST') {
			const b = await readJson(req);
			if (!b.name || typeof b.targetCents !== 'number' || b.targetCents <= 0)
				return problem(res, 400, 'Validation failed');
			const accountId = (b.accountId as string | null) || null;
			if (accountId) {
				const a = accounts.get(accountId);
				if (!a || a.householdId !== hh)
					return send(res, 400, {
						title: 'Validation failed',
						status: 400,
						errors: { accountId: ['Unknown account'] }
					});
			}
			const g: Goal = {
				id: randomUUID(),
				householdId: hh,
				name: String(b.name),
				targetCents: b.targetCents,
				targetDate: (b.targetDate as string | null) || null,
				accountId,
				savedCents: 0,
				color: String(b.color ?? '#1baf7a'),
				icon: String(b.icon ?? 'piggy-bank'),
				createdBy: user.id,
				createdAt: nowIso(),
				archived: false,
				contributions: []
			};
			goals.set(g.id, g);
			record(
				hh,
				user,
				'goal.created',
				'goal',
				g.id,
				`created goal ${g.name} · ${$(g.targetCents)}`
			);
			return send(res, 201, goalView(g));
		}
		mm = /^\/v1\/goals\/([^/]+)(\/contribute)?$/.exec(p);
		if (mm) {
			const g = goals.get(mm[1]);
			if (!g || g.householdId !== hh || g.archived) return problem(res, 404, 'Not found');
			if (mm[2] && m === 'POST') {
				if (g.accountId)
					return send(res, 400, {
						title: 'Validation failed',
						status: 400,
						detail: 'Update the linked account balance instead.'
					});
				const b = await readJson(req);
				if (typeof b.amountCents !== 'number' || b.amountCents === 0)
					return problem(res, 400, 'Validation failed');
				g.savedCents += b.amountCents;
				g.contributions.push({ at: nowIso(), amountCents: b.amountCents });
				record(
					hh,
					user,
					'goal.contributed',
					'goal',
					g.id,
					`${b.amountCents > 0 ? 'added' : 'took'} ${$(Math.abs(b.amountCents))} ${b.amountCents > 0 ? 'to' : 'from'} ${g.name}`
				);
				return send(res, 200, goalView(g));
			}
			if (!mm[2] && m === 'GET') return send(res, 200, goalView(g));
			if (!mm[2] && m === 'PUT') {
				const b = await readJson(req);
				Object.assign(g, {
					name: String(b.name ?? g.name),
					targetCents: typeof b.targetCents === 'number' ? b.targetCents : g.targetCents,
					targetDate: (b.targetDate as string | null) || null,
					accountId: (b.accountId as string | null) || null,
					color: String(b.color ?? g.color),
					icon: String(b.icon ?? g.icon)
				});
				record(hh, user, 'goal.updated', 'goal', g.id, `updated goal ${g.name}`);
				return send(res, 200, goalView(g));
			}
			if (!mm[2] && m === 'DELETE') {
				g.archived = true;
				record(hh, user, 'goal.deleted', 'goal', null, `removed goal ${g.name}`);
				return send(res, 204);
			}
		}

		// --- v3: linked banks
		if (p === '/v1/banks' && m === 'GET')
			return send(
				res,
				200,
				[...bankLinks.values()]
					.filter((l) => l.householdId === hh)
					.sort((a, b) => a.createdAt.localeCompare(b.createdAt))
					.map(linkView)
			);
		if (p === '/v1/banks/simplefin/claim' && m === 'POST') {
			const b = await readJson(req);
			const token = String(b.setupToken ?? '').trim();
			if (BANKS_DISABLED || token === 'demo-not-configured')
				return problem(
					res,
					503,
					'Bank sync not configured',
					'Security__DataKey is not set',
					'https://couplescash.app/problems/bank_sync_not_configured'
				);
			if (token !== 'demo-token')
				return send(res, 400, {
					title: 'Validation failed',
					status: 400,
					errors: { setupToken: ['That setup token is invalid or has expired'] }
				});
			const link: BankLinkRec = {
				id: randomUUID(),
				householdId: hh,
				provider: 'simplefin',
				name: String(b.name ?? '').trim() || 'My bank',
				createdBy: user.id,
				createdAt: nowIso(),
				lastSyncedAt: null,
				lastManualSyncAt: null,
				lastError: null,
				syncEnabled: true,
				bridgeErrors: []
			};
			bankLinks.set(link.id, link);
			bridge.set(link.id, demoBridge());
			record(hh, user, 'bank.linked', 'bank_link', link.id, `linked bank ${link.name}`);
			syncLink(link, user); // first sync inline
			return send(res, 201, linkView(link));
		}
		mm = /^\/v1\/banks\/accounts\/([^/]+)$/.exec(p);
		if (mm && m === 'PUT') {
			const ext = extAccounts.get(mm[1]);
			if (!ext || ext.householdId !== hh) return problem(res, 404, 'Not found');
			const b = await readJson(req);
			let account: Account | null = null;
			if (b.create && typeof b.create === 'object') {
				const c = b.create as { name?: string; type?: string; institution?: string };
				if (!c.name || !c.type) return problem(res, 400, 'Validation failed');
				const owed = c.type === 'credit' || c.type === 'loan';
				account = {
					id: randomUUID(),
					householdId: hh,
					name: String(c.name),
					institution: String(c.institution ?? ext.orgName),
					type: String(c.type),
					balanceCents: owed ? Math.abs(ext.balanceCents) : ext.balanceCents,
					color: '#2a78d6',
					icon: owed ? 'credit-card' : 'landmark',
					note: null,
					createdAt: nowIso(),
					updatedAt: nowIso(),
					archived: false
				};
				accounts.set(account.id, account);
				snapshots.set(account.id, [
					{ balanceCents: account.balanceCents, recordedAt: account.createdAt, recordedBy: user.id }
				]);
				record(
					hh,
					user,
					'account.created',
					'account',
					account.id,
					`added account ${account.name} · ${$(account.balanceCents)}`
				);
			} else if ('accountId' in b) {
				const id = (b.accountId as string | null) || null;
				if (id) {
					const a = accounts.get(id);
					if (!a || a.householdId !== hh || a.archived)
						return send(res, 400, {
							title: 'Validation failed',
							status: 400,
							errors: { accountId: ['Unknown account'] }
						});
					account = a;
				}
			} else return problem(res, 400, 'Validation failed');
			applyMapping(ext, account, user.id);
			if (account)
				record(
					hh,
					user,
					'account.balance',
					'account',
					account.id,
					`mapped ${ext.name} to ${account.name} · ${$(account.balanceCents)}`
				);
			return send(res, 200, extView(ext));
		}
		mm = /^\/v1\/banks\/([^/]+)(\/sync)?$/.exec(p);
		if (mm) {
			const link = bankLinks.get(mm[1]);
			if (!link || link.householdId !== hh) return problem(res, 404, 'Not found');
			if (mm[2] && m === 'POST') {
				const force = url.searchParams.has('force');
				const last = link.lastManualSyncAt ? new Date(link.lastManualSyncAt).getTime() : 0;
				const wait = last + SYNC_COOLDOWN_MS - Date.now();
				if (!force && wait > 0) {
					res.writeHead(429, {
						'content-type': 'application/problem+json',
						'retry-after': String(Math.ceil(wait / 1000))
					});
					res.end(
						JSON.stringify({
							title: 'Synced recently',
							status: 429,
							detail: `Try again in ${Math.ceil(wait / 60_000)} min`
						})
					);
					return;
				}
				link.lastManualSyncAt = nowIso();
				return send(res, 200, syncLink(link, user));
			}
			if (!mm[2] && m === 'PATCH') {
				const b = await readJson(req);
				if (typeof b.name === 'string' && b.name.trim()) link.name = b.name.trim().slice(0, 40);
				if (typeof b.syncEnabled === 'boolean') link.syncEnabled = b.syncEnabled;
				return send(res, 200, linkView(link));
			}
			if (!mm[2] && m === 'DELETE') {
				for (const [id, x] of extAccounts) if (x.bankLinkId === link.id) extAccounts.delete(id);
				bankLinks.delete(link.id);
				bridge.delete(link.id);
				record(hh, user, 'bank.unlinked', 'bank_link', null, `unlinked bank ${link.name}`);
				return send(res, 204);
			}
		}

		// --- v3: inbox
		if (p === '/v1/inbox/summary' && m === 'GET') return send(res, 200, inboxSummary(hh));
		if (p === '/v1/inbox' && m === 'GET') {
			const status = url.searchParams.get('status') ?? 'pending';
			const before = url.searchParams.get('before');
			const limit = Math.min(100, Number(url.searchParams.get('limit') ?? 50));
			const cursor = (t: Tx) => `${t.postedOn}|${t.createdAt}|${t.id}`;
			const all = [...txs.values()]
				.filter(
					(t) => t.householdId === hh && t.status === status && (!before || cursor(t) < before)
				)
				.sort((a, b) => cursor(b).localeCompare(cursor(a)));
			const page = all.slice(0, limit);
			return send(res, 200, {
				items: page.map(txView),
				nextBefore: all.length > limit ? cursor(page[page.length - 1]) : null
			});
		}
		if (p === '/v1/inbox/bulk' && m === 'POST') {
			const b = await readJson(req);
			const ids = Array.isArray(b.ids) ? new Set(b.ids as string[]) : null;
			const targets = [...txs.values()].filter(
				(t) => t.householdId === hh && t.status === 'pending' && (!ids || ids.has(t.id))
			);
			let confirmed = 0;
			let ignored = 0;
			let skipped = 0;
			for (const t of targets) {
				if (b.action === 'ignore') {
					t.status = 'ignored';
					ignored++;
					continue;
				}
				const s = t.suggestion;
				if (!s || s.kind === 'transfer' || t.confidence < 70) {
					skipped++;
					continue;
				}
				const err = confirmTx(
					hh,
					user,
					t,
					s.kind === 'category'
						? { as: 'expense', categoryId: s.categoryId }
						: { as: 'occurrence', itemId: s.itemId, dueDate: s.dueDate }
				);
				if (err) skipped++;
				else confirmed++;
			}
			if (b.action === 'ignore' && ignored)
				record(
					hh,
					user,
					'transaction.ignored',
					'transaction',
					null,
					`ignored ${ignored} transactions`
				);
			return send(res, 200, { confirmed, ignored, skipped });
		}
		mm = /^\/v1\/inbox\/([^/]+)\/(confirm|ignore|restore)$/.exec(p);
		if (mm && m === 'POST') {
			const tx = txs.get(mm[1]);
			if (!tx || tx.householdId !== hh) return problem(res, 404, 'Not found');
			if (mm[2] === 'confirm') {
				const err = confirmTx(hh, user, tx, await readJson(req));
				if (err) return problem(res, err.status, 'Cannot confirm', err.detail);
			} else if (mm[2] === 'ignore') {
				if (tx.status !== 'pending') return problem(res, 400, 'Not pending');
				tx.status = 'ignored';
				record(
					hh,
					user,
					'transaction.ignored',
					'transaction',
					tx.id,
					`ignored ${tx.payee ?? tx.description}`
				);
			} else {
				if (tx.status === 'pending') return problem(res, 400, 'Already pending');
				restoreTx(hh, user, tx);
			}
			return send(res, 200, txView(tx));
		}

		// --- v3: CSV import
		if (p === '/v1/imports/csv' && m === 'GET')
			return send(
				res,
				200,
				[...csvImports.values()]
					.filter((c) => c.householdId === hh)
					.sort((a, b) => b.createdAt.localeCompare(a.createdAt))
					.slice(0, 20)
					.map((c) => ({
						id: c.id,
						filename: c.filename,
						rowCount: c.rowCount,
						imported: c.imported,
						duplicates: c.duplicates,
						createdAt: c.createdAt,
						createdByName: users.get(c.createdBy)?.displayName ?? 'Someone'
					}))
			);
		if (p === '/v1/imports/csv' && m === 'POST') {
			const b = await readJson(req);
			const account = accounts.get(String(b.accountId ?? ''));
			if (!account || account.householdId !== hh || account.archived)
				return send(res, 400, {
					title: 'Validation failed',
					status: 400,
					errors: { accountId: ['Unknown account'] }
				});
			const rows = Array.isArray(b.rows) ? (b.rows as Record<string, unknown>[]) : [];
			if (rows.length > 5000) return problem(res, 400, 'Validation failed', 'Too many rows');
			let imported = 0;
			let duplicates = 0;
			let skipped = 0;
			const fresh: Tx[] = [];
			for (const r of rows) {
				const postedOn = String(r.postedOn ?? '');
				const amountCents = r.amountCents;
				const description = String(r.description ?? '').trim();
				if (
					!/^\d{4}-\d{2}-\d{2}$/.test(postedOn) ||
					typeof amountCents !== 'number' ||
					!Number.isInteger(amountCents) ||
					amountCents === 0 ||
					!description
				) {
					skipped++;
					continue;
				}
				const tx = insertTx(hh, {
					source: 'csv',
					externalAccountId: null,
					accountId: account.id,
					externalId: null,
					postedOn,
					amountCents,
					description,
					payee: (r.payee as string | undefined)?.trim() || null,
					memo: (r.memo as string | undefined)?.trim() || null,
					pending: false
				});
				if (tx) {
					fresh.push(tx);
					imported++;
				} else duplicates++;
			}
			detectTransfers(hh, fresh);
			const rec: CsvImportRec = {
				id: randomUUID(),
				householdId: hh,
				accountId: account.id,
				filename: String(b.filename ?? 'import.csv').slice(0, 120),
				rowCount: rows.length,
				imported,
				duplicates,
				createdBy: user.id,
				createdAt: nowIso()
			};
			csvImports.set(rec.id, rec);
			record(
				hh,
				user,
				'import.csv',
				'csv_import',
				rec.id,
				`imported ${rec.filename} into ${account.name} · ${imported} new`,
				{ imported, duplicates, skipped }
			);
			return send(res, 201, {
				id: rec.id,
				rowCount: rows.length,
				imported,
				duplicates,
				skipped,
				inboxPending: inboxSummary(hh).pending
			});
		}

		// --- month report
		if (p === '/v1/reports/month' && m === 'GET') {
			const month = url.searchParams.get('month') ?? todayIso().slice(0, 7);
			const [first, last] = monthRange(month);
			const summary = summaryFor(hh, month);
			const prevEnd = new Date(parse(first).getTime() - 1).toISOString();
			const hasHistory = [...accounts.values()].some(
				(a) =>
					a.householdId === hh && (snapshots.get(a.id) ?? []).some((s) => s.recordedAt <= prevEnd)
			);
			const previousNetWorthCents = hasHistory ? netWorthAt(hh, prevEnd) : null;
			const monthExpenses = hhExpenses(hh, first, last);
			const occ = calendar(hh, first, last).filter((o) => o.kind !== 'income');
			return send(res, 200, {
				month,
				summary,
				previousNetWorthCents,
				netWorthDeltaCents:
					previousNetWorthCents === null ? null : summary.netWorthCents - previousNetWorthCents,
				expensesTotalCents: monthExpenses.reduce((s, e) => s + e.amountCents, 0),
				topExpenses: [...monthExpenses]
					.sort((a, b) => b.amountCents - a.amountCents)
					.slice(0, 5)
					.map(expenseView),
				budgets: budgetRows(hh, month).rows,
				autoPaid: occ.filter((o) => o.paid?.auto),
				paidCount: occ.filter((o) => o.paid).length,
				unpaidCount: occ.filter((o) => !o.paid).length,
				generatedAt: nowIso()
			});
		}

		return problem(res, 404, 'Not found');
	} catch (e) {
		console.error(e);
		return problem(res, 500, 'Mock API error', String(e));
	}
});

server.listen(PORT, '127.0.0.1', () =>
	console.log(`mock api listening on http://127.0.0.1:${PORT}`)
);
