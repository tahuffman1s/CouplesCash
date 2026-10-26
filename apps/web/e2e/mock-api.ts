/**
 * In-memory mock of the CouplesCash API (enough of docs/API-CONTRACT.md, v1 + v2, for e2e tests).
 * Started by Playwright as a webServer on port 8099. Run manually: node e2e/mock-api.ts
 *
 * Test-only helpers (never in the real API):
 *   POST /__reset                 wipe everything
 *   POST /__mock/autopay?date=    run the autopay scheduler for that day (default: today)
 *   POST /__mock/bank-sync        run the bank-sync scheduler phase for every link (ignores cooldown);
 *                                 the first call also posts "JIFFY LUBE #42" −54.20 (today) to the
 *                                 demo checking account, so an e2e can plan a one-off first
 * Env: MOCK_PUSH=1 serves a fake VAPID key (default: 503 push_not_configured). Users whose email
 *      starts with `push-` always get push, so one e2e can cover the prefs form while the rest of
 *      the suite keeps the not-configured state.
 *      MOCK_BANKS_DISABLED=1 → POST /v1/banks/simplefin/claim answers 503 bank_sync_not_configured
 *      (the setup token `demo-not-configured` does the same per request, so one e2e can cover it).
 *
 * v3: a fake SimpleFIN Bridge lives in memory. The setup token `demo-token` claims a link with two
 * accounts (checking + credit card) and a handful of transactions; anything else is 400.
 *
 * v4: `/v1/week`, `/v1/budgets/week`, weekly budget rows (`?period=weekly`), `weeklySpendCents`,
 * `payeeHint` (seeds an item payee rule), `weekSummaryEnabled`, occurrence `varianceCents` + `status`,
 * and the wider matcher window for one-offs (±7 days, max(15 %, $10)).
 *
 * v5: the setup token `demo-v5` (see `demoBridgeV5`), the pending lifecycle (`reconcileAccount`),
 * transfers (auto-ignored pairs, keyword / learned one-sided transfers, `autoIgnoreTransfers`),
 * `/accounts/{id}/transactions`, candidates / match / transfer / unmatch, `/recurring/from-transaction`,
 * bill payments, occurrence candidates / link, `/transfers`, `availableCents` / `pendingOutCents`.
 *   POST /__mock/bank-sync?phase=post|drop|pair|brokerage[&email=]  edit the v5 Bridge, then sync
 *                                 (only that user's household when `email` is given)
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
	weeklySpendCents: number | null;
	autoIgnoreTransfers: boolean;
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
	payeeHint: string | null;
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
	weekSummaryEnabled: boolean;
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
	categoryId: string | null;
	categoryName: string | null;
	itemName: string | null;
};
type IgnoreReason = 'manual' | 'transfer' | 'dropped' | 'superseded';
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
	// v5
	ignoreReason: IgnoreReason | null;
	transferPeerId: string | null;
	transferAuto: boolean;
	/** the other side the matcher found while auto-ignore was off (a suggestion, not a pairing) */
	suggestedPeerId: string | null;
};
type PayeeRule = {
	householdId: string;
	payeeNorm: string;
	categoryId: string | null;
	itemId: string | null;
	hits: number;
	/** v5: marked as a one-sided transfer */
	isTransfer?: boolean;
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
const budgets = new Map<string, number>(); // `${hh}:${categoryId}` (monthly)
const weeklyBudgets = new Map<string, number>(); // `${hh}:${categoryId}` (v4 weekly)
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
const pushFor = (u: User) => PUSH_ENABLED || u.email.startsWith('push-');
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
		payAnchorDate: h.payAnchorDate,
		weeklySpendCents: h.weeklySpendCents,
		autoIgnoreTransfers: h.autoIgnoreTransfers
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
		payAnchorDate: null,
		weeklySpendCents: null,
		autoIgnoreTransfers: true
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
		updatedAt: i.updatedAt,
		payeeHint: i.payeeHint
	};
}
/** v4: a payee hint seeds an item payee rule so the first bank match auto-applies; clearing removes it. */
function applyPayeeHint(i: Item, hint: string | null) {
	// drop the rule the previous hint created (only if it still points at this item)
	for (const [key, rule] of payeeRules)
		if (rule.householdId === i.householdId && rule.itemId === i.id && rule.hits <= 1)
			payeeRules.delete(key);
	i.payeeHint = hint && hint.trim() ? hint.trim().slice(0, 80) : null;
	if (!i.payeeHint) return;
	const norm = normPayee(i.payeeHint);
	if (!norm) return;
	payeeRules.set(`${i.householdId}:${norm}`, {
		householdId: i.householdId,
		payeeNorm: norm,
		categoryId: null,
		itemId: i.id,
		hits: 1
	});
}
function occurrence(i: Item, date: string) {
	const c = i.categoryId ? categories.get(i.categoryId) : undefined;
	const a = i.accountId ? accounts.get(i.accountId) : undefined;
	const paid = payments.get(`${i.id}:${date}`) ?? null;
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
		paid,
		// v4
		varianceCents: paid ? paid.amountCents - i.amountCents : null,
		status: paid
			? ('cleared' as const)
			: date < todayIso()
				? ('overdue' as const)
				: ('planned' as const)
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
	// v5: the bank's available balance, pending outflows and whether bank/CSV rows land here
	const ext = [...extAccounts.values()].find((x) => x.accountId === a.id);
	const owed = a.type === 'credit' || a.type === 'loan';
	const pendingOutCents = [...txs.values()]
		.filter(
			(t) =>
				t.accountId === a.id &&
				t.pending &&
				t.amountCents < 0 &&
				(t.status === 'pending' || t.status === 'confirmed')
		)
		.reduce((s, t) => s - t.amountCents, 0);
	return {
		...rest,
		availableCents: ext && !owed && ext.availableCents !== null ? ext.availableCents : null,
		pendingOutCents,
		linked: !!ext || [...txs.values()].some((t) => t.accountId === a.id)
	};
}
/** v5: spendable uses the bank's available balance when there is one. */
function spendableOf(a: Account): number {
	return accountView(a).availableCents ?? a.balanceCents;
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
		p = {
			remindersEnabled: true,
			daysBefore: [1],
			hourLocal: 9,
			activityEnabled: true,
			weekSummaryEnabled: true
		};
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

// ---- v4: week view + weekly budgets (PLAN-V4 "Concepts") ------------------------------------
const addDaysIso = (iso: string, n: number) => {
	const d = parse(iso);
	d.setDate(d.getDate() + n);
	return toIso(d);
};
function weekStartFor(iso: string, weekStart: 0 | 1): string {
	const col = (parse(iso).getDay() - weekStart + 7) % 7;
	return addDaysIso(iso, -col);
}
const isLiquid = (a: Account) => a.type === 'checking' || a.type === 'cash';
function liquidNow(hh: string): number {
	return [...accounts.values()]
		.filter((a) => a.householdId === hh && !a.archived && isLiquid(a))
		.reduce((s, a) => s + spendableOf(a), 0);
}
/** Latest checking + cash snapshots at or before local midnight of `iso`; null when there are none. */
function liquidAt(hh: string, iso: string): number | null {
	const at = parse(iso).toISOString();
	let total = 0;
	let any = false;
	for (const a of accounts.values()) {
		if (a.householdId !== hh || a.archived || !isLiquid(a)) continue;
		const snap = (snapshots.get(a.id) ?? []).find((s) => s.recordedAt <= at);
		if (!snap) continue;
		any = true;
		total += snap.balanceCents;
	}
	return any ? total : null;
}
type WeekBudgetRow = {
	categoryId: string | null;
	name: string;
	color: string;
	icon: string;
	budgetCents: number;
	spentCents: number;
	varianceCents: number;
	percent: number;
};
function weekRow(
	categoryId: string | null,
	name: string,
	color: string,
	icon: string,
	budgetCents: number,
	spentCents: number
): WeekBudgetRow {
	return {
		categoryId,
		name,
		color,
		icon,
		budgetCents,
		spentCents,
		varianceCents: spentCents - budgetCents,
		percent:
			budgetCents > 0 ? Math.round((spentCents / budgetCents) * 100) : spentCents > 0 ? 100 : 0
	};
}
/** Spent in a week = expenses + paid bill/subscription occurrences (actual amounts) due in the week. */
function weekSpent(hh: string, start: string, categoryId: string | null) {
	const end = addDaysIso(start, 6);
	const exp = hhExpenses(hh, start, end)
		.filter((e) => categoryId === null || e.categoryId === categoryId)
		.reduce((s, e) => s + e.amountCents, 0);
	const occ = calendar(hh, start, end)
		.filter(
			(o) => o.kind !== 'income' && o.paid && (categoryId === null || o.categoryId === categoryId)
		)
		.reduce((s, o) => s + (o.paid?.amountCents ?? 0), 0);
	return exp + occ;
}
function weekBudgetFor(hh: string, start: string) {
	const h = households.get(hh)!;
	const cats = [...categories.values()]
		.filter((c) => c.householdId === hh && weeklyBudgets.has(`${hh}:${c.id}`))
		.sort((a, b) => a.sortOrder - b.sortOrder);
	const rows = cats.map((c) =>
		weekRow(
			c.id,
			c.name,
			c.color,
			c.icon,
			weeklyBudgets.get(`${hh}:${c.id}`)!,
			weekSpent(hh, start, c.id)
		)
	);
	const overallBudget = h.weeklySpendCents;
	const totalBudgetCents = rows.reduce((s, r) => s + r.budgetCents, 0);
	// judged against today's budgets: the overall target when set, else the budgeted categories only
	const historyBudget = overallBudget ?? totalBudgetCents;
	const history =
		overallBudget === null && cats.length === 0
			? []
			: Array.from({ length: 8 }, (_, i) => {
					const ws = addDaysIso(start, -7 * (7 - i));
					const spentCents =
						overallBudget !== null
							? weekSpent(hh, ws, null)
							: cats.reduce((sum, c) => sum + weekSpent(hh, ws, c.id), 0);
					return {
						weekStart: ws,
						budgetCents: historyBudget,
						spentCents,
						varianceCents: spentCents - historyBudget
					};
				});
	return {
		overall:
			overallBudget === null
				? null
				: weekRow(
						null,
						'Weekly spending',
						'#2a78d6',
						'target',
						overallBudget,
						weekSpent(hh, start, null)
					),
		categories: rows,
		totalBudgetCents,
		totalSpentCents: rows.reduce((s, r) => s + r.spentCents, 0),
		history
	};
}
function weekViewFor(hh: string, start: string) {
	const h = households.get(hh)!;
	const end = addDaysIso(start, 6);
	const today = todayIso();
	const isCurrent = start <= today && today <= end;
	const isPast = end < today;
	const occ = calendar(hh, start, end);
	const days = Array.from({ length: 7 }, (_, i) => {
		const date = addDaysIso(start, i);
		const planned = occ.filter((o) => o.dueDate === date);
		const exp = hhExpenses(hh, date, date).map(expenseView);
		return {
			date,
			planned,
			expenses: exp,
			plannedOutCents: planned
				.filter((o) => o.kind !== 'income')
				.reduce((s, o) => s + o.amountCents, 0),
			plannedInCents: planned
				.filter((o) => o.kind === 'income')
				.reduce((s, o) => s + o.amountCents, 0),
			unplannedCents: exp.reduce((s, e) => s + e.amountCents, 0)
		};
	});
	const income = occ.filter((o) => o.kind === 'income');
	const out = occ.filter((o) => o.kind !== 'income');
	const sum = (list: Occ[], f: (o: Occ) => number) => list.reduce((s, o) => s + f(o), 0);
	const incomeExpectedCents = sum(income, (o) => o.amountCents);
	const incomeReceivedCents = sum(
		income.filter((o) => o.paid),
		(o) => o.paid?.amountCents ?? 0
	);
	const plannedOutCents = sum(out, (o) => o.amountCents);
	const paidOutCents = sum(
		out.filter((o) => o.paid),
		(o) => o.paid?.amountCents ?? 0
	);
	const remainingOutCents = sum(
		out.filter((o) => !o.paid),
		(o) => o.amountCents
	);
	const unplannedCents = days.reduce((s, d) => s + d.unplannedCents, 0);
	const spendableNowCents = liquidNow(hh);
	const startingBalanceCents = isPast ? liquidAt(hh, start) : null;
	let leftAtEndCents: number | null;
	if (isPast) {
		leftAtEndCents =
			startingBalanceCents === null
				? null
				: startingBalanceCents + incomeReceivedCents - paidOutCents - unplannedCents;
	} else if (isCurrent) {
		// only what is still due from today on; unpaid items earlier in the week show as overdue rows
		const ahead = occ.filter((o) => !o.paid && o.dueDate >= today);
		leftAtEndCents =
			spendableNowCents +
			sum(
				ahead.filter((o) => o.kind === 'income'),
				(o) => o.amountCents
			) -
			sum(
				ahead.filter((o) => o.kind !== 'income'),
				(o) => o.amountCents
			);
	} else {
		const ahead = calendar(hh, today, end).filter((o) => !o.paid);
		leftAtEndCents =
			spendableNowCents +
			sum(
				ahead.filter((o) => o.kind === 'income'),
				(o) => o.amountCents
			) -
			sum(
				ahead.filter((o) => o.kind !== 'income'),
				(o) => o.amountCents
			);
	}
	// open items (any kind) due before the viewed week and before today, oldest first, max 50
	const overdueTo = addDaysIso(start < today ? start : today, -1);
	const overdue = isPast
		? []
		: calendar(hh, addDaysIso(overdueTo, -90), overdueTo)
				.filter((o) => !o.paid)
				.slice(0, 50);
	return {
		start,
		end,
		weekStart: h.weekStart,
		isCurrent,
		isPast,
		today,
		spendableNowCents,
		startingBalanceCents,
		incomeExpectedCents,
		incomeReceivedCents,
		plannedOutCents,
		paidOutCents,
		remainingOutCents,
		unplannedCents,
		leftAtEndCents,
		projected: start > today,
		days,
		overdue,
		budget: weekBudgetFor(hh, start)
	};
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

/** A date `n` days ago, but never before the first of this month (keeps e2e rows in one month). */
function recent(n: number): string {
	const first = `${todayIso().slice(0, 7)}-01`;
	const d = daysAgo(n);
	return d < first ? first : d;
}
/**
 * v5 (`demo-v5`): checking + savings with a pending diner bill (−40.00, posts at −48.00 with a new id in
 * `?phase=post`), a fuel hold older than 5 days (removed in `?phase=drop`), a transfer pair between the
 * two accounts, a one-sided brokerage transfer and a gym charge for "Create a bill from this".
 */
function demoBridgeV5(): BridgeAccount[] {
	const first = `${todayIso().slice(0, 7)}-01`;
	return [
		{
			id: 'v5-checking',
			org: 'Demo Bank',
			name: 'Everyday Checking',
			currency: 'USD',
			balance: 250000,
			available: 245900,
			transactions: [
				{ id: 'v5-rent', posted: first, amount: -185000, description: 'RENT PAYMENT' },
				{ id: 'v5-kroger', posted: recent(2), amount: -6723, description: 'KROGER #456' },
				{ id: 'v5-gym', posted: recent(2), amount: -2499, description: 'PLANET FITNESS' },
				{
					id: 'v5-diner-auth',
					posted: recent(1),
					amount: -4000,
					description: 'THE DINER',
					pending: true
				},
				{
					id: 'v5-shell-hold',
					posted: daysAgo(6),
					amount: -100,
					description: 'SHELL OIL 57442',
					pending: true
				},
				{
					id: 'v5-xfer-out',
					posted: recent(1),
					amount: -50000,
					description: 'ONLINE TRANSFER TO SAV REF 123'
				},
				{
					id: 'v5-brokerage',
					posted: recent(4),
					amount: -100000,
					description: 'TRANSFER TO BROKERAGE'
				}
			]
		},
		{
			id: 'v5-savings',
			org: 'Demo Bank',
			name: 'Way2Save Savings',
			currency: 'USD',
			balance: 1200000,
			available: 1200000,
			transactions: [
				{
					id: 'v5-xfer-in',
					posted: recent(1),
					amount: 50000,
					description: 'ONLINE TRANSFER FROM CHK REF 123'
				}
			]
		}
	];
}
/** `?phase=` for POST /__mock/bank-sync: edits a v5 Bridge before the sync runs. */
function applyPhase(data: BridgeAccount[], phase: string) {
	const checking = data.find((a) => a.id === 'v5-checking');
	const savings = data.find((a) => a.id === 'v5-savings');
	if (!checking) return;
	const add = (acc: BridgeAccount, tx: BridgeTx) => {
		if (!acc.transactions.some((t) => t.id === tx.id)) acc.transactions.push(tx);
	};
	if (phase === 'post') {
		// the authorization disappears and the posted charge (with the tip) arrives under a new id
		checking.transactions = checking.transactions.filter((t) => t.id !== 'v5-diner-auth');
		add(checking, {
			id: 'v5-diner-posted',
			posted: todayIso(),
			amount: -4800,
			description: 'THE DINER'
		});
	} else if (phase === 'drop') {
		checking.transactions = checking.transactions.filter((t) => t.id !== 'v5-shell-hold');
	} else if (phase === 'pair' && savings) {
		add(checking, {
			id: 'v5-xfer-out-2',
			posted: todayIso(),
			amount: -25000,
			description: 'ONLINE TRANSFER TO SAV REF 456'
		});
		add(savings, {
			id: 'v5-xfer-in-2',
			posted: todayIso(),
			amount: 25000,
			description: 'ONLINE TRANSFER FROM CHK REF 456'
		});
	} else if (phase === 'brokerage') {
		add(checking, {
			id: 'v5-brokerage-2',
			posted: todayIso(),
			amount: -20000,
			description: 'TRANSFER TO BROKERAGE'
		});
	}
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
	// 1. recurring occurrence within ±5 days, amount within max(2 %, $1), kind matches sign;
	//    v4: one-offs get ±7 days and max(15 %, $10)
	let best: { item: Item; date: string; distance: number } | null = null;
	for (const i of items.values()) {
		if (i.householdId !== hh || i.archived) continue;
		if ((i.kind === 'income') === outflow) continue;
		const once = i.frequency === 'once';
		const tolerance = once
			? Math.max(Math.round(i.amountCents * 0.15), 1000)
			: Math.max(Math.round(i.amountCents * 0.02), 100);
		if (Math.abs(i.amountCents - abs) > tolerance) continue;
		const window = once ? 7 : 5;
		const from = toIso(new Date(parse(tx.postedOn).getTime() - window * 86400_000));
		const to = toIso(new Date(parse(tx.postedOn).getTime() + window * 86400_000));
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

const isHidden = (t: Tx) =>
	t.status === 'ignored' && (t.ignoreReason === 'dropped' || t.ignoreReason === 'superseded');
/** Which account a row belongs to for transfer detection: the mapped account, else the bank's account. */
const accountKey = (t: Tx) => t.accountId ?? t.externalAccountId;
const dayDistance = (a: string, b: string) =>
	Math.round(Math.abs(parse(a).getTime() - parse(b).getTime()) / 86400_000);
/** v5 one-sided transfer keywords (Zelle/Venmo/Cash App are payments to people, not transfers). */
const TRANSFER_KEYWORDS = /\b(transfer|xfer)\b|payment thank you|autopay payment|online pmt/i;

function markTransfer(t: Tx, peer: Tx | null, auto: boolean, by: string | null) {
	t.status = 'ignored';
	t.ignoreReason = 'transfer';
	t.transferPeerId = peer?.id ?? null;
	t.transferAuto = auto;
	t.suggestedPeerId = null;
	t.confirmed = {
		expenseId: null,
		itemId: null,
		dueDate: null,
		by,
		at: nowIso(),
		auto,
		categoryId: null,
		categoryName: null,
		itemName: null
	};
}

/**
 * v5 transfers: a pair (other account, opposite sign, same |amount|, ≤ 3 days) is auto-ignored on both
 * sides when the household has `autoIgnoreTransfers`, else suggested (80). Unpaired rows with a learned
 * transfer payee are auto-ignored (85) or suggested; transfer keywords are a suggestion at 60.
 */
function detectTransfers(hh: string, fresh: Tx[]) {
	const auto = households.get(hh)?.autoIgnoreTransfers !== false;
	const open = () =>
		[...txs.values()].filter(
			(t) => t.householdId === hh && t.status === 'pending' && !t.suggestedPeerId
		);
	let autoPairs = 0;
	for (const a of fresh) {
		if (a.status !== 'pending' || a.suggestedPeerId || !accountKey(a)) continue;
		const b = open().find(
			(o) =>
				o.id !== a.id &&
				accountKey(o) &&
				accountKey(o) !== accountKey(a) &&
				o.amountCents === -a.amountCents &&
				dayDistance(o.postedOn, a.postedOn) <= 3
		);
		if (!b) continue;
		if (auto) {
			markTransfer(a, b, true, null);
			markTransfer(b, a, true, null);
			autoPairs++;
		} else {
			for (const [t, peer] of [
				[a, b],
				[b, a]
			] as const) {
				t.suggestion = { kind: 'transfer', confidence: 80 };
				t.confidence = 80;
				t.suggestedPeerId = peer.id;
			}
		}
	}
	if (autoPairs)
		record(
			hh,
			null,
			'transfer.auto',
			'transfer',
			null,
			`ignored ${autoPairs * 2} transfers (auto)`,
			{ pairs: autoPairs }
		);
	for (const t of fresh) {
		if (t.status !== 'pending' || t.suggestedPeerId) continue;
		if (t.suggestion?.kind === 'occurrence') continue;
		const rule = payeeRules.get(`${hh}:${normPayee(t.payee ?? t.description)}`);
		if (rule?.isTransfer) {
			if (auto) {
				markTransfer(t, null, true, null);
				record(
					hh,
					null,
					'transfer.auto',
					'transfer',
					t.id,
					`ignored ${t.payee ?? t.description} as a transfer (auto)`
				);
			} else {
				t.suggestion = { kind: 'transfer', confidence: 85 };
				t.confidence = 85;
			}
		} else if (
			TRANSFER_KEYWORDS.test(t.description) ||
			(t.payee && TRANSFER_KEYWORDS.test(t.payee))
		) {
			t.suggestion = { kind: 'transfer', confidence: 60 };
			t.confidence = 60;
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
		| 'ignoreReason'
		| 'transferPeerId'
		| 'transferAuto'
		| 'suggestedPeerId'
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
		createdAt: nowIso(),
		ignoreReason: null,
		transferPeerId: null,
		transferAuto: false,
		suggestedPeerId: null
	};
	txs.set(tx.id, tx);
	return tx;
}

/** Mapping an external account pushes its balance into the CouplesCash account (+ snapshot). */
function applyMapping(ext: ExtAccount, account: Account | null, by: string) {
	ext.accountId = account?.id ?? null;
	ext.updatedAt = nowIso();
	// every row from this bank account follows the mapping (v5: the account Transactions tab lists them)
	for (const t of txs.values()) if (t.externalAccountId === ext.id) t.accountId = ext.accountId;
	if (!account) return;
	const owed = account.type === 'credit' || account.type === 'loan';
	const cents = owed ? Math.abs(ext.balanceCents) : ext.balanceCents;
	if (cents !== account.balanceCents) setBalance(account, cents, by);
}

/** Normalised token overlap ≥ 0.5, or the same first significant token (PLAN-V5 §1). */
function similarDescriptions(a: string, b: string): boolean {
	const ta = normPayee(a)
		.split(' ')
		.filter((w) => w.length > 1 && w !== 'the');
	const tb = normPayee(b)
		.split(' ')
		.filter((w) => w.length > 1 && w !== 'the');
	if (!ta.length || !tb.length) return false;
	if (ta[0] === tb[0]) return true;
	const shared = ta.filter((w) => tb.includes(w)).length;
	return shared / Math.min(ta.length, tb.length) >= 0.5;
}
/** A confirmed row's amount changed: the expense / the occurrence payment follows it. */
function followAmount(t: Tx) {
	const c = t.confirmed;
	if (!c || t.status !== 'confirmed') return;
	if (c.expenseId) {
		const e = expenses.get(c.expenseId);
		if (e) {
			e.amountCents = Math.abs(t.amountCents);
			e.spentOn = t.postedOn;
			e.updatedAt = nowIso();
		}
	}
	if (c.itemId && c.dueDate) {
		const pay = payments.get(`${c.itemId}:${c.dueDate}`);
		if (pay && pay.source === 'bank') pay.amountCents = Math.abs(t.amountCents);
	}
}
/**
 * v5 pending lifecycle for one bank account (PendingReconciler): same id refreshes in place; a new
 * posted id replaces an absent pending row in place (keeps id, status and confirmation); pending rows
 * absent for more than 5 days are dropped (confirmation reversed). Returns the newly inserted rows.
 */
function reconcileAccount(hh: string, ext: ExtAccount, ba: BridgeAccount): Tx[] {
	const fresh: Tx[] = [];
	const seen = new Set(
		ba.transactions.map((bt) => fingerprintFor('simplefin', bt.id, null, '', 0, ''))
	);
	const mine = () =>
		[...txs.values()].filter(
			(t) => t.householdId === hh && t.externalAccountId === ext.id && !isHidden(t)
		);
	const usedPending = new Set<string>();
	for (const bt of ba.transactions) {
		const fp = fingerprintFor('simplefin', bt.id, null, '', 0, '');
		const existing = [...txs.values()].find((t) => t.householdId === hh && t.fingerprint === fp);
		if (existing) {
			// same id: refresh in place (pending → posted, amount changes)
			const amountChanged = existing.amountCents !== bt.amount;
			existing.pending = !!bt.pending;
			existing.amountCents = bt.amount;
			existing.postedOn = bt.posted;
			if (amountChanged) followAmount(existing);
			continue;
		}
		if (!bt.pending) {
			const abs = Math.abs(bt.amount);
			const match = mine()
				.filter(
					(t) =>
						t.pending &&
						!seen.has(t.fingerprint) &&
						!usedPending.has(t.id) &&
						Math.sign(t.amountCents) === Math.sign(bt.amount) &&
						bt.posted >= addDaysIso(t.postedOn, -1) &&
						bt.posted <= addDaysIso(t.postedOn, 7) &&
						Math.abs(Math.abs(t.amountCents) - abs) <=
							Math.max(Math.round(Math.abs(t.amountCents) * 0.25), 500) &&
						similarDescriptions(t.payee ?? t.description, bt.payee ?? bt.description)
				)
				.sort(
					(a, b) =>
						Math.abs(Math.abs(a.amountCents) - abs) - Math.abs(Math.abs(b.amountCents) - abs) ||
						dayDistance(a.postedOn, bt.posted) - dayDistance(b.postedOn, bt.posted)
				)[0];
			if (match) {
				usedPending.add(match.id);
				const before = match.amountCents;
				match.externalId = bt.id;
				match.fingerprint = fp;
				match.amountCents = bt.amount;
				match.postedOn = bt.posted;
				match.description = bt.description;
				match.payee = bt.payee ?? null;
				match.memo = bt.memo ?? null;
				match.pending = false;
				if (before !== bt.amount) {
					followAmount(match);
					record(
						hh,
						null,
						'transaction.posted',
						'transaction',
						match.id,
						`${match.payee ?? match.description} posted at ${money(hh, Math.abs(bt.amount))} (was ${money(hh, Math.abs(before))})`,
						{ fromCents: before, toCents: bt.amount }
					);
				}
				continue;
			}
		}
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
	// authorizations that fell off: absent, still pending, older than 5 days → dropped
	const cutoff = addDaysIso(todayIso(), -5);
	for (const t of mine()) {
		if (!t.pending || seen.has(t.fingerprint) || usedPending.has(t.id) || t.postedOn >= cutoff)
			continue;
		reverseTx(t);
		t.status = 'ignored';
		t.ignoreReason = 'dropped';
		record(
			hh,
			null,
			'transaction.dropped',
			'transaction',
			t.id,
			`${t.payee ?? t.description} · ${money(hh, Math.abs(t.amountCents))} never posted (dropped)`
		);
	}
	return fresh;
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
		fresh.push(...reconcileAccount(hh, ext, ba));
	}
	detectTransfers(hh, fresh);
	// auto-mark: recurring match ≥ 90 whose item already has a learned payee rule
	for (const tx of fresh) {
		if (tx.status !== 'pending') continue;
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
			categoryId: null,
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
		confirmed:
			t.status === 'pending' || !t.confirmed
				? null
				: { ...t.confirmed, byName: by?.displayName ?? null },
		createdAt: t.createdAt,
		// v5
		ignoreReason: t.status === 'ignored' ? (t.ignoreReason ?? 'manual') : null,
		transfer:
			t.status === 'ignored' && t.ignoreReason === 'transfer'
				? (() => {
						const peer = t.transferPeerId ? txs.get(t.transferPeerId) : undefined;
						const peerAccount = peer?.accountId ? accounts.get(peer.accountId) : undefined;
						return {
							peerId: peer?.id ?? null,
							peerAccountId: peerAccount?.id ?? null,
							peerAccountName: peerAccount?.name ?? null,
							auto: t.transferAuto
						};
					})()
				: null,
		match:
			t.status === 'confirmed' && t.confirmed?.itemId && t.confirmed.dueDate
				? {
						itemId: t.confirmed.itemId,
						itemName: t.confirmed.itemName ?? items.get(t.confirmed.itemId)?.name ?? '',
						itemKind: items.get(t.confirmed.itemId)?.kind ?? 'bill',
						dueDate: t.confirmed.dueDate
					}
				: null,
		expense:
			t.status === 'confirmed' && t.confirmed?.expenseId
				? {
						expenseId: t.confirmed.expenseId,
						categoryId: t.confirmed.categoryId,
						categoryName: t.confirmed.categoryName
					}
				: null
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
		rule.isTransfer = false;
	}
	if ('itemId' in patch) {
		rule.itemId = patch.itemId ?? null;
		rule.categoryId = null;
		rule.isTransfer = false;
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
			categoryId: cat?.id ?? null,
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
			categoryId: null,
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
/**
 * v5: undoes whatever a row was confirmed as (expense deleted — deducted = false, so no balance to put
 * back —, bank payment removed, transfer peer back to pending) and leaves it pending.
 */
function reverseTx(t: Tx) {
	const c = t.status === 'confirmed' ? t.confirmed : null;
	if (c?.expenseId) expenses.delete(c.expenseId);
	if (c?.itemId && c.dueDate) {
		const key = `${c.itemId}:${c.dueDate}`;
		if (payments.get(key)?.source === 'bank') payments.delete(key);
	}
	if (t.transferPeerId) {
		const peer = txs.get(t.transferPeerId);
		if (peer && peer.transferPeerId === t.id) {
			peer.status = 'pending';
			peer.ignoreReason = null;
			peer.transferPeerId = null;
			peer.transferAuto = false;
			peer.confirmed = null;
		}
	}
	t.status = 'pending';
	t.ignoreReason = null;
	t.transferPeerId = null;
	t.transferAuto = false;
	t.suggestedPeerId = null;
	t.confirmed = null;
}
function restoreTx(hh: string, user: User | null, tx: Tx) {
	reverseTx(tx);
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
			t.ignoreReason = null;
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

// ---- v5: candidates, account transactions, transfer report, bill payments --------------------
/** Ranked occurrences ±days around a row: amount closeness (55) + date distance (35) + payee rule (10). */
function occurrenceCandidates(hh: string, t: Tx, days: number) {
	const outflow = t.amountCents < 0;
	const abs = Math.abs(t.amountCents);
	const rule = payeeRules.get(`${hh}:${normPayee(t.payee ?? t.description)}`);
	const from = addDaysIso(t.postedOn, -days);
	const to = addDaysIso(t.postedOn, days);
	const out: {
		itemId: string;
		itemName: string;
		kind: string;
		dueDate: string;
		amountCents: number;
		paid: Payment | null;
		paidByThis: boolean;
		score: number;
		distance: number;
	}[] = [];
	for (const i of items.values()) {
		if (i.householdId !== hh || i.archived) continue;
		if ((i.kind === 'income') === outflow) continue;
		for (const d of expand(i, from, to)) {
			const paid = payments.get(`${i.id}:${d}`) ?? null;
			const paidByThis =
				t.status === 'confirmed' && t.confirmed?.itemId === i.id && t.confirmed.dueDate === d;
			const diff = Math.abs(i.amountCents - abs) / Math.max(i.amountCents, abs, 1);
			const distance = dayDistance(d, t.postedOn);
			const score = Math.round(
				Math.max(0, 1 - diff * 2) * 55 +
					Math.max(0, 1 - distance / Math.max(days, 1)) * 35 +
					(rule?.itemId === i.id ? 10 : 0)
			);
			out.push({
				itemId: i.id,
				itemName: i.name,
				kind: i.kind,
				dueDate: d,
				amountCents: i.amountCents,
				paid,
				paidByThis,
				score: Math.min(100, score),
				distance
			});
		}
	}
	return out
		.sort((a, b) => b.score - a.score || a.distance - b.distance)
		.slice(0, 20)
		.map(({ distance: _d, ...c }) => c);
}
/** The other side of a transfer: another account, opposite sign, |amount| within 1 %, ±5 days, unpaired. */
function isTransferPeer(t: Tx, o: Tx): boolean {
	const abs = Math.abs(t.amountCents);
	return (
		o.id !== t.id &&
		o.householdId === t.householdId &&
		!isHidden(o) &&
		!!accountKey(o) &&
		accountKey(o) !== accountKey(t) &&
		Math.sign(o.amountCents) === -Math.sign(t.amountCents) &&
		Math.abs(Math.abs(o.amountCents) - abs) <= Math.round(abs * 0.01) &&
		dayDistance(o.postedOn, t.postedOn) <= 5 &&
		(!o.transferPeerId || o.transferPeerId === t.id)
	);
}
function transferPeers(t: Tx): Tx[] {
	return [...txs.values()]
		.filter((o) => isTransferPeer(t, o))
		.sort(
			(a, b) =>
				Number(b.id === t.suggestedPeerId || b.id === t.transferPeerId) -
					Number(a.id === t.suggestedPeerId || a.id === t.transferPeerId) ||
				dayDistance(a.postedOn, t.postedOn) - dayDistance(b.postedOn, t.postedOn)
		);
}
/** POST /inbox/{id}/transfer: pair with a peer, or one-sided (learns the payee). */
function transferTx(hh: string, user: User, t: Tx, peer: Tx | null) {
	reverseTx(t);
	if (peer) {
		reverseTx(peer);
		markTransfer(peer, t, false, user.id);
	}
	markTransfer(t, peer, false, user.id);
	if (!peer) {
		const norm = normPayee(t.payee ?? t.description);
		if (norm) {
			const key = `${hh}:${norm}`;
			const rule = payeeRules.get(key) ?? {
				householdId: hh,
				payeeNorm: norm,
				categoryId: null,
				itemId: null,
				hits: 0
			};
			rule.isTransfer = true;
			rule.categoryId = null;
			rule.itemId = null;
			rule.hits++;
			payeeRules.set(key, rule);
		}
	}
	const from = t.amountCents < 0 ? t : peer;
	const to = t.amountCents < 0 ? peer : t;
	const name = (x: Tx | null) =>
		(x?.accountId ? accounts.get(x.accountId)?.name : null) ?? 'another account';
	record(
		hh,
		user,
		'transfer.paired',
		'transfer',
		t.id,
		peer
			? `marked a transfer ${name(from)} → ${name(to)} · ${money(hh, Math.abs(t.amountCents))}`
			: `marked ${t.payee ?? t.description} as a transfer · ${money(hh, Math.abs(t.amountCents))}`,
		{ peerId: peer?.id ?? null }
	);
}
/** A typed expense on an account, shaped like a bank row (`kind: 'manual'`, id = expense id). */
function manualRow(e: Expense) {
	const a = e.accountId ? accounts.get(e.accountId) : undefined;
	const c = e.categoryId ? categories.get(e.categoryId) : undefined;
	return {
		id: e.id,
		source: 'manual' as const,
		accountId: a?.id ?? null,
		accountName: a?.name ?? null,
		postedOn: e.spentOn,
		amountCents: -e.amountCents,
		description: e.name,
		payee: null,
		memo: e.note,
		pending: false,
		status: 'confirmed' as const,
		suggestion: null,
		confirmed: {
			expenseId: e.id,
			itemId: null,
			dueDate: null,
			by: e.createdBy,
			byName: users.get(e.createdBy)?.displayName ?? null,
			at: e.createdAt,
			auto: false,
			categoryId: c?.id ?? null,
			categoryName: c?.name ?? null,
			itemName: null
		},
		createdAt: e.createdAt,
		ignoreReason: null,
		transfer: null,
		match: null,
		expense: { expenseId: e.id, categoryId: c?.id ?? null, categoryName: c?.name ?? null },
		kind: 'manual' as const
	};
}
/** GET /accounts/{id}/transactions: bank rows ∪ manual expenses, newest first, keyset by (date, id). */
function accountTransactions(hh: string, accountId: string, q: URLSearchParams) {
	const filter = q.get('filter') ?? 'all';
	const search = (q.get('q') ?? '').trim().toLowerCase();
	const month = q.get('month');
	const [from, to] =
		month && /^\d{4}-\d{2}$/.test(month) ? monthRange(month) : ['0000-01-01', '9999-12-31'];
	const before = q.get('before');
	const limit = Math.min(100, Math.max(1, Number(q.get('limit') ?? 50)));
	const fromBank = new Set(
		[...txs.values()].map((t) => t.confirmed?.expenseId).filter((x): x is string => !!x)
	);
	const bank = [...txs.values()]
		.filter((t) => t.householdId === hh && t.accountId === accountId && !isHidden(t))
		.filter((t) => t.postedOn >= from && t.postedOn <= to)
		.filter((t) => {
			switch (filter) {
				case 'pending':
					return t.pending;
				case 'review':
					return t.status === 'pending';
				case 'matched':
					return t.status === 'confirmed';
				case 'transfers':
					return t.status === 'ignored' && t.ignoreReason === 'transfer';
				default:
					return true;
			}
		})
		.map((t) => ({ ...txView(t), kind: 'bank' as const }));
	const manual =
		filter === 'all' || filter === 'matched'
			? [...expenses.values()]
					.filter(
						(e) =>
							e.householdId === hh &&
							e.accountId === accountId &&
							!fromBank.has(e.id) &&
							e.spentOn >= from &&
							e.spentOn <= to
					)
					.map(manualRow)
			: [];
	const matches = (r: { description: string; payee: string | null; memo: string | null }) =>
		!search || [r.description, r.payee, r.memo].some((v) => v && v.toLowerCase().includes(search));
	const cursor = (r: { postedOn: string; id: string }) => `${r.postedOn}|${r.id}`;
	const all = [...bank, ...manual]
		.filter(matches)
		.sort((a, b) => cursor(b).localeCompare(cursor(a)));
	const pageRows = all.filter((r) => !before || cursor(r) < before).slice(0, limit + 1);
	const more = pageRows.length > limit;
	const items = pageRows.slice(0, limit);
	return {
		items,
		nextBefore: more ? cursor(items[items.length - 1]) : null,
		totals: {
			inCents: all.filter((r) => r.amountCents > 0).reduce((s, r) => s + r.amountCents, 0),
			outCents: all.filter((r) => r.amountCents < 0).reduce((s, r) => s - r.amountCents, 0),
			pendingOutCents: all
				.filter(
					(r) =>
						r.pending && r.amountCents < 0 && (r.status === 'pending' || r.status === 'confirmed')
				)
				.reduce((s, r) => s - r.amountCents, 0),
			count: all.length
		}
	};
}
/** GET /transfers?month=: pairs (dated by the outflow leg), one-sided legs, flows and net per account. */
function transferReport(hh: string, month: string) {
	const [from, to] = monthRange(month);
	const legs = [...txs.values()].filter(
		(t) => t.householdId === hh && t.status === 'ignored' && t.ignoreReason === 'transfer'
	);
	const OTHER = 'Other account';
	const nameOf = (t: Tx) => (t.accountId ? (accounts.get(t.accountId)?.name ?? OTHER) : OTHER);
	const used = new Set<string>();
	const pairs: {
		id: string;
		date: string;
		amountCents: number;
		from: { accountId: string; name: string; transactionId: string };
		to: { accountId: string; name: string; transactionId: string };
		auto: boolean;
	}[] = [];
	const pairLegs: [Tx, Tx][] = [];
	const oneSided: Tx[] = [];
	for (const leg of legs.sort(
		(a, b) => b.postedOn.localeCompare(a.postedOn) || b.id.localeCompare(a.id)
	)) {
		if (used.has(leg.id)) continue;
		const peer = leg.transferPeerId ? txs.get(leg.transferPeerId) : undefined;
		if (peer && peer.transferPeerId === leg.id && !used.has(peer.id)) {
			used.add(leg.id);
			used.add(peer.id);
			const [o, i] = leg.amountCents < 0 ? [leg, peer] : [peer, leg];
			if (o.postedOn >= from && o.postedOn <= to) {
				pairLegs.push([o, i]);
				pairs.push({
					id: o.id,
					date: o.postedOn,
					amountCents: Math.abs(o.amountCents),
					from: { accountId: o.accountId ?? '', name: nameOf(o), transactionId: o.id },
					to: { accountId: i.accountId ?? '', name: nameOf(i), transactionId: i.id },
					auto: o.transferAuto || i.transferAuto
				});
			}
			continue;
		}
		used.add(leg.id);
		if (leg.postedOn >= from && leg.postedOn <= to) oneSided.push(leg);
	}
	type Flow = {
		fromAccountId: string | null;
		fromName: string;
		toAccountId: string | null;
		toName: string;
		totalCents: number;
		count: number;
	};
	const flows = new Map<string, Flow>();
	const addFlow = (
		fromId: string | null,
		fromName: string,
		toId: string | null,
		toName: string,
		cents: number
	) => {
		const key = `${fromId}|${toId}`;
		const f = flows.get(key) ?? {
			fromAccountId: fromId,
			fromName,
			toAccountId: toId,
			toName,
			totalCents: 0,
			count: 0
		};
		f.totalCents += cents;
		f.count++;
		flows.set(key, f);
	};
	for (const [o, i] of pairLegs)
		addFlow(o.accountId, nameOf(o), i.accountId, nameOf(i), Math.abs(o.amountCents));
	for (const l of oneSided)
		if (l.amountCents < 0) addFlow(l.accountId, nameOf(l), null, OTHER, Math.abs(l.amountCents));
		else addFlow(null, OTHER, l.accountId, nameOf(l), l.amountCents);
	const net = new Map<
		string,
		{ accountId: string; name: string; inCents: number; outCents: number }
	>();
	for (const l of [...pairLegs.flat(), ...oneSided]) {
		if (!l.accountId) continue;
		const n = net.get(l.accountId) ?? {
			accountId: l.accountId,
			name: nameOf(l),
			inCents: 0,
			outCents: 0
		};
		if (l.amountCents >= 0) n.inCents += l.amountCents;
		else n.outCents -= l.amountCents;
		net.set(l.accountId, n);
	}
	const flowList = [...flows.values()].sort(
		(a, b) => b.totalCents - a.totalCents || a.fromName.localeCompare(b.fromName)
	);
	return {
		month,
		totalCents: flowList.reduce((s, f) => s + f.totalCents, 0),
		flows: flowList,
		netByAccount: [...net.values()]
			.map((n) => ({ ...n, netCents: n.inCents - n.outCents }))
			.sort((a, b) => a.name.localeCompare(b.name)),
		pairs,
		oneSided: oneSided.map(txView)
	};
}
/** Bill detail Payments: the next occurrence after today, then the last `limit` on or before today. */
function billPayments(hh: string, i: Item, limit: number) {
	const today = todayIso();
	const past = expand(i, i.anchorDate, today).slice(-limit).reverse();
	const next = expand(i, addDaysIso(today, 1), toIso(addMonthsClamp(new Date(), 24)))[0];
	const dates = next ? [next, ...past] : past;
	return dates.map((d) => {
		const paid = payments.get(`${i.id}:${d}`) ?? null;
		const t = [...txs.values()].find(
			(x) =>
				x.householdId === hh &&
				x.status === 'confirmed' &&
				x.confirmed?.itemId === i.id &&
				x.confirmed.dueDate === d
		);
		const a = t?.accountId ? accounts.get(t.accountId) : undefined;
		return {
			dueDate: d,
			plannedCents: i.amountCents,
			paid,
			varianceCents: paid ? paid.amountCents - i.amountCents : null,
			transaction:
				paid && t
					? {
							id: t.id,
							postedOn: t.postedOn,
							amountCents: t.amountCents,
							description: t.payee ?? t.description,
							accountName: a?.name ?? null
						}
					: null
		};
	});
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
				weeklyBudgets,
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
			// v5: `?phase=post|drop|pair|brokerage` edits the v5 Bridge first; `?email=` limits the run
			// to that user's household (parallel e2e workers share this process)
			const phase = url.searchParams.get('phase');
			const email = url.searchParams.get('email')?.toLowerCase();
			const only = email
				? ([...users.values()].find((u) => u.email === email)?.householdId ?? '-')
				: null;
			for (const link of bankLinks.values()) {
				if (!link.syncEnabled) continue;
				if (only && link.householdId !== only) continue;
				if (phase) {
					applyPhase(bridge.get(link.id) ?? [], phase);
					syncLink(link, null);
					synced++;
					continue;
				}
				// v4: a later statement line only the scheduler ever sees (lets an e2e plan first)
				const checking = (bridge.get(link.id) ?? []).find((a) => a.id === 'demo-checking');
				if (checking && !checking.transactions.some((t) => t.id === 'tx-jiffy'))
					checking.transactions.push({
						id: 'tx-jiffy',
						posted: todayIso(),
						amount: -5420,
						description: 'JIFFY LUBE #42'
					});
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
			if (!pushFor(user))
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
		if (p === '/v1/push/prefs' && m === 'GET') {
			if (!pushFor(user)) return problem(res, 503, 'Push not configured');
			return send(res, 200, prefsView(user.id));
		}
		if (p === '/v1/push/prefs' && m === 'PUT') {
			if (!pushFor(user)) return problem(res, 503, 'Push not configured');
			const b = await readJson(req);
			prefs.set(user.id, {
				remindersEnabled: Boolean(b.remindersEnabled),
				daysBefore: Array.isArray(b.daysBefore) ? (b.daysBefore as number[]).slice(0, 5) : [1],
				hourLocal: Number(b.hourLocal ?? 9),
				activityEnabled: Boolean(b.activityEnabled),
				weekSummaryEnabled:
					b.weekSummaryEnabled === undefined ? true : Boolean(b.weekSummaryEnabled)
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
			if ('weeklySpendCents' in b) {
				const v = b.weeklySpendCents;
				if (v !== null && (typeof v !== 'number' || !Number.isInteger(v) || v < 0))
					return send(res, 400, {
						title: 'Validation failed',
						status: 400,
						errors: { weeklySpendCents: ['Must be a whole number of cents, 0 or more'] }
					});
				h.weeklySpendCents = v as number | null;
				changed.push('weekly target');
			}
			if ('autoIgnoreTransfers' in b) {
				if (typeof b.autoIgnoreTransfers !== 'boolean')
					return send(res, 400, {
						title: 'Validation failed',
						status: 400,
						errors: { autoIgnoreTransfers: ['Must be true or false'] }
					});
				h.autoIgnoreTransfers = b.autoIgnoreTransfers;
				changed.push('transfers');
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
		let mm = /^\/v1\/accounts\/([^/]+)\/transactions$/.exec(p);
		if (mm && m === 'GET') {
			const a = accounts.get(mm[1]);
			if (!a || a.householdId !== hh || a.archived) return problem(res, 404, 'Not found');
			const filter = url.searchParams.get('filter') ?? 'all';
			if (!['all', 'pending', 'review', 'matched', 'transfers'].includes(filter))
				return send(res, 400, {
					title: 'Validation failed',
					status: 400,
					errors: { filter: ['Unknown filter'] }
				});
			return send(res, 200, accountTransactions(hh, a.id, url.searchParams));
		}
		mm = /^\/v1\/accounts\/([^/]+)(\/balance|\/history)?$/.exec(p);
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
				weeklyBudgets.delete(`${hh}:${c.id}`);
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
				payeeHint: null,
				createdAt: nowIso(),
				updatedAt: nowIso(),
				archived: false
			};
			items.set(i.id, i);
			applyPayeeHint(i, (b.payeeHint as string | null | undefined) ?? null);
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
		// --- v5: create a bill from a transaction, payments, link a transaction
		if (p === '/v1/recurring/from-transaction' && m === 'POST') {
			const b = await readJson(req);
			const t = txs.get(String(b.transactionId ?? ''));
			if (!t || t.householdId !== hh) return problem(res, 404, 'Not found');
			if (isHidden(t)) return problem(res, 400, 'Cannot create a bill', 'This row was dropped');
			const outflow = t.amountCents < 0;
			const kind = String(b.kind ?? (outflow ? 'bill' : 'income'));
			if ((kind === 'income') === outflow)
				return send(res, 400, {
					title: 'Validation failed',
					status: 400,
					errors: { kind: ['Money out becomes a bill or subscription, money in an income'] }
				});
			const name = (String(b.name ?? '').trim() || t.payee || t.description).slice(0, 80);
			const i: Item = {
				id: randomUUID(),
				householdId: hh,
				name,
				kind,
				amountCents: Math.abs(t.amountCents),
				categoryId: null,
				accountId: t.accountId,
				frequency: String(b.frequency ?? 'monthly'),
				anchorDate: t.postedOn,
				endDate: null,
				autoPay: false,
				notes: null,
				url: null,
				payeeHint: null,
				createdAt: nowIso(),
				updatedAt: nowIso(),
				archived: false
			};
			items.set(i.id, i);
			applyPayeeHint(i, t.payee ?? t.description);
			reverseTx(t);
			confirmTx(hh, user, t, { as: 'occurrence', itemId: i.id, dueDate: i.anchorDate });
			record(
				hh,
				user,
				'recurring.from_transaction',
				'recurring',
				i.id,
				`added ${i.kind} ${i.name} · ${$(i.amountCents)} from a transaction`,
				{ transactionId: t.id }
			);
			return send(res, 201, { item: itemView(i), transaction: txView(t) });
		}
		mm = /^\/v1\/recurring\/([^/]+)\/payments$/.exec(p);
		if (mm && m === 'GET') {
			const i = items.get(mm[1]);
			if (!i || i.householdId !== hh || i.archived) return problem(res, 404, 'Not found');
			const limit = Math.min(60, Math.max(1, Number(url.searchParams.get('limit') ?? 12)));
			return send(res, 200, billPayments(hh, i, limit));
		}
		mm = /^\/v1\/recurring\/([^/]+)\/occurrences\/(\d{4}-\d{2}-\d{2})\/(candidates|link)$/.exec(p);
		if (mm) {
			const i = items.get(mm[1]);
			const date = mm[2];
			if (!i || i.householdId !== hh || i.archived) return problem(res, 404, 'Not found');
			if (!expand(i, date, date).includes(date)) return problem(res, 404, 'Not an occurrence');
			if (mm[3] === 'candidates' && m === 'GET') {
				const days = Math.min(60, Math.max(1, Number(url.searchParams.get('days') ?? 10)));
				const income = i.kind === 'income';
				const ranked = [...txs.values()]
					.filter(
						(t) =>
							t.householdId === hh &&
							// to review, or confirmed as an expense (not paying a bill, not a transfer)
							(t.status === 'pending' || (t.status === 'confirmed' && !!t.confirmed?.expenseId)) &&
							t.amountCents > 0 === income &&
							dayDistance(t.postedOn, date) <= days
					)
					.map((t) => {
						const abs = Math.abs(t.amountCents);
						const diff = Math.abs(i.amountCents - abs) / Math.max(i.amountCents, abs, 1);
						return {
							t,
							score:
								Math.max(0, 1 - diff * 2) * 60 + (1 - dayDistance(t.postedOn, date) / days) * 40
						};
					})
					.sort((a, b) => b.score - a.score)
					.slice(0, 20)
					.map((r) => txView(r.t));
				return send(res, 200, ranked);
			}
			if (mm[3] === 'link' && m === 'POST') {
				const b = await readJson(req);
				const t = txs.get(String(b.transactionId ?? ''));
				if (!t || t.householdId !== hh) return problem(res, 404, 'Not found');
				if (payments.has(`${i.id}:${date}`))
					return problem(res, 409, 'Already paid', 'That occurrence is already paid');
				if ((i.kind === 'income') !== t.amountCents > 0)
					return problem(
						res,
						400,
						'Cannot link',
						'That transaction does not match the direction of this bill'
					);
				reverseTx(t);
				const err = confirmTx(hh, user, t, { as: 'occurrence', itemId: i.id, dueDate: date });
				if (err) return problem(res, err.status, 'Cannot link', err.detail);
				return send(res, 200, occurrence(i, date));
			}
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
					if ('payeeHint' in b) applyPayeeHint(i, (b.payeeHint as string | null) ?? null);
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
		// v4: the budget section of the week view on its own
		if (p === '/v1/budgets/week' && m === 'GET') {
			const h = households.get(hh)!;
			const start = weekStartFor(url.searchParams.get('start') ?? todayIso(), h.weekStart);
			return send(res, 200, weekBudgetFor(hh, start));
		}
		mm = /^\/v1\/budgets\/([^/]+)$/.exec(p);
		if (mm) {
			const c = categories.get(mm[1]);
			if (!c || c.householdId !== hh) return problem(res, 404, 'Not found');
			const month = url.searchParams.get('month') ?? todayIso().slice(0, 7);
			const weekly = url.searchParams.get('period') === 'weekly';
			if (weekly && m === 'PUT') {
				const b = await readJson(req);
				if (typeof b.amountCents !== 'number' || b.amountCents < 0)
					return problem(res, 400, 'Validation failed');
				weeklyBudgets.set(`${hh}:${c.id}`, b.amountCents);
				record(
					hh,
					user,
					'budget.set',
					'budget',
					c.id,
					`set the ${c.name} weekly budget to ${$(b.amountCents)}`
				);
				const start = weekStartFor(todayIso(), households.get(hh)!.weekStart);
				const row = weekBudgetFor(hh, start).categories.find((r) => r.categoryId === c.id);
				return send(res, 200, row);
			}
			if (weekly && m === 'DELETE') {
				weeklyBudgets.delete(`${hh}:${c.id}`);
				record(hh, user, 'budget.cleared', 'budget', c.id, `cleared the ${c.name} weekly budget`);
				return send(res, 204);
			}
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

		// --- v4: week view
		if (p === '/v1/week' && m === 'GET') {
			const h = households.get(hh)!;
			const start = url.searchParams.get('start') ?? todayIso();
			if (!/^\d{4}-\d{2}-\d{2}$/.test(start)) return problem(res, 400, 'Validation failed');
			return send(res, 200, weekViewFor(hh, weekStartFor(start, h.weekStart)));
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
				.reduce((s, a) => s + spendableOf(a), 0);
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
			if (token !== 'demo-token' && token !== 'demo-v5')
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
			bridge.set(link.id, token === 'demo-v5' ? demoBridgeV5() : demoBridge());
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
			const includeHidden = url.searchParams.get('includeHidden') === '1';
			const all = [...txs.values()]
				.filter(
					(t) =>
						t.householdId === hh &&
						t.status === status &&
						(includeHidden || !isHidden(t)) &&
						(!before || cursor(t) < before)
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
					t.ignoreReason = 'manual';
					ignored++;
					continue;
				}
				const s = t.suggestion;
				if (!s || t.confidence < 70) {
					skipped++;
					continue;
				}
				if (s.kind === 'transfer') {
					// a detected pair (auto-ignore off) is ignored on both sides
					const peer = t.suggestedPeerId ? txs.get(t.suggestedPeerId) : undefined;
					if (t.status !== 'pending') continue;
					markTransfer(t, peer && peer.status === 'pending' ? peer : null, false, user.id);
					if (peer && peer.status === 'pending') {
						markTransfer(peer, t, false, user.id);
						if (!ids || ids.has(peer.id)) ignored++;
					}
					ignored++;
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
				tx.ignoreReason = 'manual';
				tx.suggestedPeerId = null;
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

		// --- v5: matching from anywhere
		mm = /^\/v1\/inbox\/([^/]+)\/candidates$/.exec(p);
		if (mm && m === 'GET') {
			const tx = txs.get(mm[1]);
			if (!tx || tx.householdId !== hh) return problem(res, 404, 'Not found');
			const days = Math.min(60, Math.max(1, Number(url.searchParams.get('days') ?? 30)));
			return send(res, 200, {
				occurrences: occurrenceCandidates(hh, tx, days),
				transferPeers: transferPeers(tx).map(txView)
			});
		}
		mm = /^\/v1\/inbox\/([^/]+)\/(match|transfer|unmatch)$/.exec(p);
		if (mm && m === 'POST') {
			const tx = txs.get(mm[1]);
			if (!tx || tx.householdId !== hh) return problem(res, 404, 'Not found');
			const b = await readJson(req);
			if (mm[2] === 'match') {
				const i = items.get(String(b.itemId ?? ''));
				const date = String(b.dueDate ?? '');
				if (!i || i.householdId !== hh || i.archived)
					return send(res, 400, {
						title: 'Validation failed',
						status: 400,
						errors: { itemId: ['Unknown bill'] }
					});
				if ((i.kind === 'income') !== tx.amountCents > 0)
					return problem(
						res,
						400,
						'Cannot match',
						'That bill does not match the direction of this transaction'
					);
				if (!expand(i, date, date).includes(date))
					return send(res, 400, {
						title: 'Validation failed',
						status: 400,
						errors: { dueDate: ['Not an occurrence'] }
					});
				const byThis =
					tx.status === 'confirmed' &&
					tx.confirmed?.itemId === i.id &&
					tx.confirmed.dueDate === date;
				if (byThis) return send(res, 200, txView(tx));
				if (payments.has(`${i.id}:${date}`))
					return problem(res, 409, 'Already paid', 'That occurrence is already paid');
				reverseTx(tx);
				const err = confirmTx(hh, user, tx, { as: 'occurrence', itemId: i.id, dueDate: date });
				if (err) return problem(res, err.status, 'Cannot match', err.detail);
				return send(res, 200, txView(tx));
			}
			if (mm[2] === 'transfer') {
				if (isHidden(tx))
					return problem(res, 400, 'Cannot mark as transfer', 'This authorization was dropped');
				const peerId = (b.peerId as string | null | undefined) || null;
				let peer: Tx | null = null;
				if (peerId) {
					const found = txs.get(peerId);
					if (!found || found.householdId !== hh)
						return send(res, 400, {
							title: 'Validation failed',
							status: 400,
							errors: { peerId: ['Unknown transaction'] }
						});
					if (!isTransferPeer(tx, found))
						return send(res, 400, {
							title: 'Validation failed',
							status: 400,
							errors: {
								peerId: [
									'The other side must be on another account, opposite in sign, within 1 % and 5 days'
								]
							}
						});
					peer = found;
				}
				transferTx(hh, user, tx, peer);
				return send(res, 200, txView(tx));
			}
			// unmatch: back to To review from any status, reversing everything (peer too)
			if (tx.status !== 'pending') {
				const wasTransfer = tx.status === 'ignored' && tx.ignoreReason === 'transfer';
				reverseTx(tx);
				record(
					hh,
					user,
					wasTransfer ? 'transfer.unpaired' : 'transaction.unmatched',
					'transaction',
					tx.id,
					wasTransfer
						? `${tx.payee ?? tx.description} is not a transfer`
						: `put ${tx.payee ?? tx.description} back to review`
				);
			}
			return send(res, 200, txView(tx));
		}
		if (p === '/v1/transfers' && m === 'GET') {
			const month = url.searchParams.get('month') ?? todayIso().slice(0, 7);
			if (!/^\d{4}-(0[1-9]|1[0-2])$/.test(month))
				return send(res, 400, {
					title: 'Validation failed',
					status: 400,
					errors: { month: ['Use YYYY-MM'] }
				});
			return send(res, 200, transferReport(hh, month));
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
