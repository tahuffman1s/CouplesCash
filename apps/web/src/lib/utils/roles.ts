/**
 * v7 account roles: smart defaults for "Tell us which account is which" (pure, unit-tested).
 * Savings-looking names → Savings; of the checking accounts the busiest one (most posted
 * transactions in the last feed, then the biggest balance) → Bills; other checking → Fun money,
 * one per person (a member's name in the account name wins, then in order); cards, loans and
 * investments → Other. Roles the server already has are kept.
 */
import type { AccountRole, AccountType } from '#lib/types.ts';

export type RoleRow = {
	key: string;
	name: string;
	org?: string | null;
	type?: AccountType | null;
	balanceCents?: number | null;
	/** posted transactions in the last bank feed (busier = more likely the bills account) */
	posted?: number | null;
	/** what the server says today (`other` = nothing chosen yet) */
	role?: AccountRole | null;
	ownerUserId?: string | null;
};
/** One row of "which account is which": a CouplesCash account, or a bank account not tracked yet. */
export type RoleRowView = RoleRow & {
	/** `a:<accountId>` for an account, `x:<externalAccountId>` for an untracked bank account */
	key: string;
	org: string;
	balanceCents: number;
	accountId: string | null;
	externalId: string | null;
	linked: boolean;
};
export type RoleChoice = { role: AccountRole; ownerUserId: string | null };
export type RoleMember = { id: string; displayName: string };

const SAVINGS_RE = /\b(sav(?:ings?)?|high[- ]yield|hys[a]?|money market|mma|cd)\b/i;
const OTHER_RE =
	/\b(credit|card|visa|mastercard|amex|discover|loan|mortgage|brokerage|invest(?:ment)?s?|401k?|ira|roth)\b/i;

/** What an account looks like from its name and type alone. */
export function accountShape(
	row: Pick<RoleRow, 'name' | 'type'>
): 'savings' | 'checking' | 'other' {
	if (row.type === 'savings' || SAVINGS_RE.test(row.name)) return 'savings';
	if (row.type && row.type !== 'checking' && row.type !== 'cash') return 'other';
	if (OTHER_RE.test(row.name)) return 'other';
	return 'checking';
}

/** True when the server already holds a real choice (anything but all `other`). */
export function rolesChosen(rows: Pick<RoleRow, 'role'>[]): boolean {
	return rows.some((r) => r.role && r.role !== 'other');
}

export function guessRoles(rows: RoleRow[], members: RoleMember[]): Record<string, RoleChoice> {
	const out: Record<string, RoleChoice> = {};
	if (rolesChosen(rows)) {
		for (const r of rows)
			out[r.key] = { role: r.role ?? 'other', ownerUserId: r.ownerUserId ?? null };
		return out;
	}
	const checking: RoleRow[] = [];
	for (const r of rows) {
		const shape = accountShape(r);
		if (shape === 'checking') checking.push(r);
		else out[r.key] = { role: shape === 'savings' ? 'savings' : 'other', ownerUserId: null };
	}
	const busiest = checking.toSorted(
		(a, b) =>
			(b.posted ?? 0) - (a.posted ?? 0) ||
			(b.balanceCents ?? 0) - (a.balanceCents ?? 0) ||
			a.name.localeCompare(b.name)
	);
	const [bills, ...fun] = busiest;
	if (bills) out[bills.key] = { role: 'bills', ownerUserId: null };
	// fun accounts keep their original order (the bank lists them as "…4411", "…4412")
	const funRows = checking.filter((r) => fun.includes(r));
	const owners = assignOwners(funRows, members);
	for (const r of funRows) out[r.key] = { role: 'fun', ownerUserId: owners[r.key] ?? null };
	return out;
}

/** A member's first name inside the account name wins; the rest go to the remaining members in order. */
export function assignOwners(rows: Pick<RoleRow, 'key' | 'name'>[], members: RoleMember[]) {
	const owners: Record<string, string | null> = {};
	const free = [...members];
	const rest: Pick<RoleRow, 'key' | 'name'>[] = [];
	for (const r of rows) {
		const hit = free.find((m) => {
			const first = m.displayName.trim().split(/\s+/)[0]?.toLowerCase();
			return !!first && first.length >= 2 && new RegExp(`\\b${escape(first)}\\b`, 'i').test(r.name);
		});
		if (hit) {
			owners[r.key] = hit.id;
			free.splice(free.indexOf(hit), 1);
		} else rest.push(r);
	}
	for (const r of rest) owners[r.key] = free.shift()?.id ?? null;
	return owners;
}
const escape = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/** What has to be true before the roles can be saved: at most one bills account. */
export function roleProblems(choices: RoleChoice[]): string[] {
	const bills = choices.filter((c) => c.role === 'bills').length;
	if (bills > 1) return ['Only one account can be the bills account'];
	return [];
}
