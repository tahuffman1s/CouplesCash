/**
 * In-memory mock of the CouplesCash API (enough of docs/API-CONTRACT.md for e2e tests).
 * Started by Playwright as a webServer on port 8099. Run manually: node e2e/mock-api.ts
 */
import { createServer, type IncomingMessage, type ServerResponse } from 'node:http';
import { createHash, randomBytes, randomUUID } from 'node:crypto';

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
type Payment = { paidAt: string; paidBy: string; amountCents: number };
type Code = {
	bytesHash: string;
	codeHash: string;
	code: string;
	payload: string;
	householdId: string;
	expiresAt: number;
	redeemed: boolean;
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

const PORT = Number(process.env.MOCK_API_PORT ?? 8099);
const ACCESS_TTL = Number(process.env.MOCK_ACCESS_TTL ?? 900);

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
	return { id: h.id, name: h.name, createdAt: h.createdAt, members };
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
		joinedAt: { [owner.id]: nowIso() }
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

// ---- recurrence -------------------------------------------------------------------------
const pad = (n: number) => (n < 10 ? `0${n}` : `${n}`);
const toIso = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
const parse = (s: string) => {
	const [y, m, d] = s.split('-').map(Number);
	return new Date(y, m - 1, d);
};
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
	const today = toIso(new Date());
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
function calendar(hh: string, from: string, to: string) {
	const out = [];
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
const problem = (res: ServerResponse, status: number, title: string, detail?: string) =>
	send(res, status, { type: 'about:blank', title, status, detail });

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
				refreshTokens
			])
				map.clear();
			codes.length = 0;
			return send(res, 204);
		}

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
			if (typeof b.displayName === 'string' && b.displayName.trim())
				user.displayName = b.displayName.trim();
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
			if (typeof b.name === 'string' && b.name.trim()) h.name = b.name.trim();
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
			return send(res, 200, authResult(user));
		}
		if (p === '/v1/couple/leave' && m === 'POST') {
			const h = user.householdId ? households.get(user.householdId) : null;
			if (h) {
				h.memberIds = h.memberIds.filter((id) => id !== user.id);
				if (h.memberIds.length === 0) households.delete(h.id);
			}
			user.householdId = null;
			return send(res, 200, authResult(user));
		}

		const hh = user.householdId;
		if (!hh) return problem(res, 409, 'No household');

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
				a.balanceCents = b.balanceCents;
				a.updatedAt = nowIso();
				snapshots.set(a.id, [
					{ balanceCents: a.balanceCents, recordedAt: a.updatedAt, recordedBy: user.id },
					...(snapshots.get(a.id) ?? [])
				]);
				return send(res, 200, accountView(a));
			}
			if (!mm[2] && m === 'GET') return send(res, 200, accountView(a));
			if (!mm[2] && m === 'PUT') {
				const b = await readJson(req);
				Object.assign(a, {
					name: b.name,
					institution: b.institution,
					type: b.type,
					balanceCents: b.balanceCents,
					color: b.color ?? a.color,
					icon: b.icon ?? a.icon,
					note: b.note ?? null,
					updatedAt: nowIso()
				});
				return send(res, 200, accountView(a));
			}
			if (!mm[2] && m === 'DELETE') {
				a.archived = true;
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
					payments.set(`${i.id}:${date}`, {
						paidAt: nowIso(),
						paidBy: user.id,
						amountCents: typeof b.amountCents === 'number' ? b.amountCents : i.amountCents
					});
					return send(res, 200, occurrence(i, date));
				}
				if (m === 'DELETE') {
					payments.delete(`${i.id}:${date}`);
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
					return send(res, 200, itemView(i));
				}
				if (m === 'DELETE') {
					i.archived = true;
					return send(res, 204);
				}
			}
		}

		// --- calendar & summary
		if (p === '/v1/calendar' && m === 'GET') {
			const from = url.searchParams.get('from') ?? toIso(new Date());
			const to = url.searchParams.get('to') ?? from;
			return send(res, 200, { from, to, occurrences: calendar(hh, from, to) });
		}
		if (p === '/v1/summary' && m === 'GET') {
			const month = url.searchParams.get('month') ?? toIso(new Date()).slice(0, 7);
			const first = `${month}-01`;
			const firstD = parse(first);
			const last = toIso(new Date(firstD.getFullYear(), firstD.getMonth() + 1, 0));
			const acc = [...accounts.values()].filter((a) => a.householdId === hh && !a.archived);
			const liab = (t: string) => t === 'credit' || t === 'loan';
			const assetsCents = acc.filter((a) => !liab(a.type)).reduce((s, a) => s + a.balanceCents, 0);
			const liabilitiesCents = acc
				.filter((a) => liab(a.type))
				.reduce((s, a) => s + a.balanceCents, 0);
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
			const byCat = new Map<
				string | null,
				{
					categoryId: string | null;
					name: string;
					color: string;
					amountCents: number;
					count: number;
				}
			>();
			for (const i of active) {
				if (i.kind === 'income') continue;
				const c = i.categoryId ? categories.get(i.categoryId) : undefined;
				const key = c?.id ?? null;
				const row = byCat.get(key) ?? {
					categoryId: key,
					name: c?.name ?? 'Uncategorised',
					color: c?.color ?? '#9ca3af',
					amountCents: 0,
					count: 0
				};
				row.amountCents += monthlyEq(i.amountCents, i.frequency);
				row.count += 1;
				byCat.set(key, row);
			}
			const byKind = ['bill', 'subscription', 'income'].map((kind) => ({
				kind,
				amountCents: eq(kind),
				count: active.filter((i) => i.kind === kind).length
			}));
			const today = toIso(new Date());
			const in14 = toIso(new Date(Date.now() + 14 * 86400_000));
			const upcoming = calendar(hh, today, in14)
				.sort((a, b) => Number(!!a.paid) - Number(!!b.paid) || a.dueDate.localeCompare(b.dueDate))
				.slice(0, 20);
			return send(res, 200, {
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
