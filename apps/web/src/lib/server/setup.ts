/**
 * v7 setup + settings, server side: the data the setup steps need, and the actions shared by
 * `/setup` and `/settings` (roles, payday + paycheck, fun money, pasted notes, bank claim).
 * Every action answers `fail(…)` with a message or `{ ok: true }`; the caller redirects.
 */
import { fail, type RequestEvent } from '@sveltejs/kit';
import type {
	Account,
	AccountRole,
	AccountType,
	BankLink,
	BillSuggestion,
	Category,
	ExternalAccount,
	Household,
	Plan,
	RecurringItem
} from '#lib/types.ts';
import { ACCOUNT_ROLES } from '#lib/types.ts';
import { endpoints, isApiError, type RecurringBody } from './api';
import { echo, str } from './forms';
import { guessAccountType } from '#lib/utils/labels.ts';
import { parseMoney } from '#lib/utils/money.ts';
import { today } from '#lib/utils/dates.ts';
import { guessRoles, roleProblems, type RoleRowView } from '#lib/utils/roles.ts';
export type { RoleRowView };
import {
	DEFAULT_FUN_MONTHLY_CENTS,
	DEFAULT_FUN_WEEKLY_CENTS,
	detectPaycheck,
	funItemBodies,
	funItemsFor,
	lastWeekdayOnOrBefore,
	type FunPlan,
	type FunRow
} from '#lib/utils/setup.ts';
export type { FunRow };
import { firstName } from '#lib/utils/plan.ts';

/** Accounts ∪ untracked bank accounts, with smart defaults for their roles. */
export function roleRows(accounts: Account[], banks: BankLink[] | null): RoleRowView[] {
	const exts = (banks ?? []).flatMap((b) => b.accounts);
	const extFor = (accountId: string) => exts.find((x) => x.accountId === accountId);
	const rows: RoleRowView[] = accounts.map((a) => ({
		key: `a:${a.id}`,
		name: a.name,
		org: a.institution,
		type: a.type,
		balanceCents: a.availableCents ?? a.balanceCents,
		posted: extFor(a.id)?.lastFeed?.posted ?? null,
		role: a.role ?? 'other',
		ownerUserId: a.ownerUserId ?? null,
		accountId: a.id,
		externalId: extFor(a.id)?.id ?? null,
		linked: !!a.linked
	}));
	for (const x of exts.filter((x) => !x.accountId))
		rows.push({
			key: `x:${x.id}`,
			name: x.name,
			org: x.orgName,
			type: guessAccountType(x.name),
			balanceCents: x.availableCents ?? x.balanceCents,
			posted: x.lastFeed?.posted ?? null,
			role: null,
			ownerUserId: null,
			accountId: null,
			externalId: x.id,
			linked: true
		});
	const order: Record<string, number> = { bills: 0, savings: 1, fun: 2, other: 3 };
	return rows.toSorted((a, b) => (order[a.role ?? 'other'] ?? 3) - (order[b.role ?? 'other'] ?? 3));
}

export function roleDefaults(rows: RoleRowView[], household: Household | null) {
	return guessRoles(rows, household?.members ?? []);
}

/** "Save roles": tracks untracked bank accounts first (creating the account), then sets each role. */
export async function saveRoles(event: RequestEvent, data: FormData) {
	const v = endpoints(event);
	const keys = data.getAll('row').map(String).slice(0, 30);
	const choices = keys.map((key) => {
		const role = str(data, `role_${key}`) as AccountRole;
		return {
			key,
			role: ACCOUNT_ROLES.includes(role) ? role : 'other',
			ownerUserId: role === 'fun' ? str(data, `owner_${key}`) || null : null
		};
	});
	const problems = roleProblems(choices);
	if (problems.length) return fail(400, { rolesError: problems[0], values: echo(data) });
	try {
		// bills last, so moving the role between accounts always ends on the chosen one
		for (const c of choices.toSorted(
			(a, b) => Number(a.role === 'bills') - Number(b.role === 'bills')
		)) {
			let accountId: string | null = null;
			if (c.key.startsWith('a:')) accountId = c.key.slice(2);
			else if (c.key.startsWith('x:')) {
				const extId = c.key.slice(2);
				const extName = str(data, `name_${c.key}`) || 'Account';
				const org = str(data, `org_${c.key}`) || 'Bank';
				const type: AccountType =
					c.role === 'savings'
						? 'savings'
						: c.role === 'other'
							? guessAccountType(extName)
							: 'checking';
				// the bills account reads as its bank ("U.S. Bank, available now")
				const name = (c.role === 'bills' ? org : extName).slice(0, 60);
				const ext = await v.banks.map(extId, { create: { name, type, institution: org } });
				accountId = ext.accountId;
			}
			if (accountId) await v.accounts.setRole(accountId, c.role, c.ownerUserId);
		}
	} catch (e) {
		return fail(isApiError(e) ? e.status : 500, {
			rolesError: isApiError(e) ? (e.field('role') ?? e.message) : 'Could not save the accounts.',
			values: echo(data)
		});
	}
	return { ok: true as const };
}

/** What the payday step shows: the weekday and the paycheck (found in the bank when there is none). */
export async function paycheckGuess(
	event: RequestEvent,
	items: RecurringItem[],
	plan: Plan | null
): Promise<{
	amountCents: number | null;
	source: 'item' | 'found' | null;
	bankName: string | null;
}> {
	const income =
		items.find((i) => i.kind === 'income' && i.frequency === 'weekly') ??
		items.find((i) => i.kind === 'income');
	if (income)
		return {
			amountCents: income.amountCents,
			source: 'item',
			bankName: income.bankNames?.[0] ?? null
		};
	const v = endpoints(event);
	const found = await v.billSuggestions
		.list()
		.catch(() => [] as BillSuggestion[])
		.then((l) => l.find((s) => s.kind === 'income'));
	if (found) return { amountCents: found.amountCents, source: 'found', bankName: found.bankName };
	const billsId = plan?.billsAccount?.id;
	if (!billsId) return { amountCents: null, source: null, bankName: null };
	const page = await v.accounts.transactions(billsId, { limit: 100 }).catch(() => null);
	const hit = page ? detectPaycheck(page.items) : null;
	return hit
		? { amountCents: hit.amountCents, source: 'found', bankName: hit.bankName }
		: { amountCents: null, source: null, bankName: null };
}

function bodyOf(i: RecurringItem): RecurringBody {
	return {
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
		bankNames: i.bankNames ?? (i.payeeHint ? [i.payeeHint] : []),
		amountVaries: i.amountVaries ?? false,
		...(i.kind === 'transfer'
			? { fromAccountId: i.fromAccountId ?? null, toAccountId: i.toAccountId ?? null }
			: {})
	};
}

/** "Payday": the weekday (PATCH /couple) and the paycheck (the weekly income item, created or updated). */
export async function savePayday(event: RequestEvent, data: FormData) {
	const v = endpoints(event);
	const wd = Number(str(data, 'paydayWeekday'));
	if (!Number.isInteger(wd) || wd < 0 || wd > 6)
		return fail(400, { paydayError: 'Pick the day you get paid.', values: echo(data) });
	const raw = str(data, 'paycheck');
	const cents = raw ? parseMoney(raw) : null;
	if (raw && (cents === null || cents <= 0))
		return fail(400, { paydayError: 'Enter the paycheck like 2450.00.', values: echo(data) });
	try {
		await v.household.update({ paydayWeekday: wd });
		if (cents) {
			const [items, plan] = await Promise.all([v.recurring.list(), v.plan.get().catch(() => null)]);
			const anchor = lastWeekdayOnOrBefore(today(), wd);
			const income =
				items.find((i) => i.kind === 'income' && i.frequency === 'weekly') ??
				items.find((i) => i.kind === 'income');
			if (income) {
				const anchorMoved = new Date(`${income.anchorDate}T00:00`).getDay() !== wd;
				await v.recurring.update(income.id, {
					...bodyOf(income),
					amountCents: cents,
					frequency: income.frequency === 'once' ? 'weekly' : income.frequency,
					anchorDate: anchorMoved ? anchor : income.anchorDate
				});
			} else {
				const bankName = str(data, 'paycheckBankName');
				await v.recurring.create({
					name: 'Paycheck',
					kind: 'income',
					amountCents: cents,
					categoryId: str(data, 'incomeCategoryId') || null,
					accountId: plan?.billsAccount?.id ?? null,
					frequency: 'weekly',
					anchorDate: anchor,
					endDate: null,
					autoPay: false,
					notes: null,
					url: null,
					bankNames: bankName.length >= 2 ? [bankName.slice(0, 80)] : [],
					amountVaries: false
				});
			}
		}
	} catch (e) {
		return fail(isApiError(e) ? e.status : 500, {
			paydayError: isApiError(e)
				? (e.field('paydayWeekday') ?? e.message)
				: 'Could not save payday.',
			values: echo(data)
		});
	}
	return { ok: true as const };
}

/** One row per fun-money account, prefilled from its transfer items (or $20 / $150). */
export function funRows(accounts: Account[], items: RecurringItem[]): FunRow[] {
	return accounts
		.filter((a) => a.role === 'fun')
		.toSorted((a, b) => (a.ownerName ?? '~').localeCompare(b.ownerName ?? '~'))
		.map((a) => {
			const { weekly, monthly } = funItemsFor(items, a.id);
			const memberName = firstName(a.ownerName) ?? a.name;
			const has = !!(weekly || monthly);
			return {
				accountId: a.id,
				label: a.ownerName ? `Fun (${firstName(a.ownerName)})` : a.name,
				memberName,
				weeklyCents: weekly?.amountCents ?? (has ? 0 : DEFAULT_FUN_WEEKLY_CENTS),
				monthlyCents: monthly?.amountCents ?? (has ? 0 : DEFAULT_FUN_MONTHLY_CENTS),
				weeklyItemId: weekly?.id ?? null,
				monthlyItemId: monthly?.id ?? null
			};
		});
}

/** "Fun money": creates the missing transfer items (one bulk call), updates or removes the rest. */
export async function saveFun(event: RequestEvent, data: FormData) {
	const v = endpoints(event);
	try {
		const [accounts, items, plan] = await Promise.all([
			v.accounts.list(),
			v.recurring.list(),
			v.plan.get().catch(() => null)
		]);
		const billsId = plan?.billsAccount?.id ?? accounts.find((a) => a.role === 'bills')?.id;
		if (!billsId)
			return fail(400, {
				funError: 'Pick the bills account first (step 2), fun money comes out of it.',
				values: echo(data)
			});
		const rows = funRows(accounts, items);
		const create: FunPlan[] = [];
		for (const r of rows) {
			const read = (name: string) => {
				const raw = str(data, `${name}_${r.accountId}`);
				if (!raw) return 0;
				const c = parseMoney(raw);
				return c === null || c < 0 ? null : c;
			};
			const weekly = read('weekly');
			const monthly = read('monthly');
			if (weekly === null || monthly === null)
				return fail(400, { funError: 'Amounts must be numbers, like 20.', values: echo(data) });
			const plan1: FunPlan = {
				memberName: r.memberName,
				toAccountId: r.accountId,
				weeklyCents: 0,
				monthlyCents: 0
			};
			for (const [cents, itemId, key] of [
				[weekly, r.weeklyItemId, 'weeklyCents'],
				[monthly, r.monthlyItemId, 'monthlyCents']
			] as const) {
				const item = itemId ? items.find((i) => i.id === itemId) : undefined;
				if (item) {
					if (cents === 0) await v.recurring.remove(item.id);
					else if (cents !== item.amountCents)
						await v.recurring.update(item.id, { ...bodyOf(item), amountCents: cents });
				} else if (cents > 0) plan1[key] = cents;
			}
			if (plan1.weeklyCents || plan1.monthlyCents) create.push(plan1);
		}
		const payday = plan?.start ?? lastWeekdayOnOrBefore(today(), 5);
		const bodies = funItemBodies(create, { billsAccountId: billsId, payday, today: today() });
		if (bodies.length) await v.recurring.bulk(bodies);
	} catch (e) {
		return fail(isApiError(e) ? e.status : 500, {
			funError: isApiError(e) ? e.message : 'Could not save fun money.',
			values: echo(data)
		});
	}
	return { ok: true as const };
}

/**
 * "Paste your notes" → `POST /recurring/bulk`. The browser sends the corrected preview as JSON;
 * the API validates (all or nothing) and its per-index errors come back as "Line 4: …".
 */
export async function saveNotes(event: RequestEvent, data: FormData) {
	let bodies: RecurringBody[];
	let lines: number[];
	try {
		const parsed = JSON.parse(str(data, 'items') || '[]') as {
			line: number;
			body: RecurringBody;
		}[];
		if (!Array.isArray(parsed)) throw new Error('not a list');
		bodies = parsed.map((p) => p.body).slice(0, 100);
		lines = parsed.map((p) => p.line);
	} catch {
		return fail(400, { notesError: 'Could not read the list. Try again.' });
	}
	if (bodies.length === 0) return fail(400, { notesError: 'Pick at least one bill to add.' });
	try {
		await endpoints(event).recurring.bulk(bodies);
	} catch (e) {
		if (isApiError(e, 400) && e.problem.errors) {
			const [key, msgs] = Object.entries(e.problem.errors)[0] ?? [];
			const m = key ? /^items\[(\d+)\]/i.exec(key) : null;
			const line = m ? (lines[Number(m[1])] ?? Number(m[1])) + 1 : null;
			return fail(400, {
				notesError: `${line ? `Line ${line}: ` : ''}${msgs?.[0] ?? 'Check the list.'}`
			});
		}
		return fail(isApiError(e) ? e.status : 500, {
			notesError: isApiError(e) ? e.message : 'Could not add the bills.'
		});
	}
	const count = Number(str(data, 'count')) || new Set(lines).size;
	return { ok: true as const, added: count };
}

/** Link a bank (same as Settings → Banks). */
export async function claimBank(event: RequestEvent, data: FormData) {
	const setupToken = str(data, 'setupToken').replace(/\s+/g, '');
	const name = str(data, 'name') || 'My bank';
	if (!setupToken)
		return fail(400, { bankError: 'Paste the setup token first.', values: echo(data) });
	try {
		const link = await endpoints(event).banks.claim(setupToken, name.slice(0, 40));
		return { ok: true as const, linked: link.name, found: link.accounts.length };
	} catch (e) {
		if (isApiError(e, 503)) return fail(503, { notConfigured: true, values: echo(data) });
		if (isApiError(e, 400))
			return fail(400, {
				bankError: e.field('setupToken') ?? 'That setup token is invalid or has expired.',
				values: echo(data)
			});
		return fail(isApiError(e) ? e.status : 500, {
			bankError: isApiError(e) ? e.message : 'Could not link the bank.',
			values: echo(data)
		});
	}
}

/** Everything the setup steps and Settings → Accounts / Household need, in parallel, failure-tolerant. */
export async function setupData(event: RequestEvent) {
	const v = endpoints(event);
	const quiet = <T>(p: Promise<T>, fallback: T) => p.catch(() => fallback);
	const [plan, accounts, banks, items, categories] = await Promise.all([
		quiet(v.plan.get(), null as Plan | null),
		quiet(v.accounts.list(), [] as Account[]),
		quiet(v.banks.list(), null as BankLink[] | null),
		quiet(v.recurring.list(), [] as RecurringItem[]),
		quiet(v.categories(), [] as Category[])
	]);
	return { plan, accounts, banks, items, categories };
}

export type { ExternalAccount };
