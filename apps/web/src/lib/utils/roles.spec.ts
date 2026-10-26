import { describe, expect, it } from 'vitest';
import {
	accountShape,
	assignOwners,
	guessRoles,
	roleProblems,
	rolesChosen,
	type RoleRow
} from './roles.ts';

const members = [
	{ id: 'u-travis', displayName: 'Travis' },
	{ id: 'u-jess', displayName: 'Jess Huffman' }
];

/** Their banks: US Bank checking, Ally HYS, two Ally checkings. */
const theirs: RoleRow[] = [
	{ key: 'us', name: 'Easy Checking', org: 'U.S. Bank', balanceCents: 240807, posted: 12 },
	{ key: 'hys', name: 'Online Savings', org: 'Ally Bank', balanceCents: 1200000, posted: 3 },
	{ key: 'f1', name: 'Spending Account ··4411', org: 'Ally Bank', balanceCents: 8320, posted: 3 },
	{ key: 'f2', name: 'Spending Account ··4412', org: 'Ally Bank', balanceCents: 4175, posted: 3 }
];

describe('accountShape', () => {
	it('savings-looking names and types', () => {
		expect(accountShape({ name: 'Online Savings' })).toBe('savings');
		expect(accountShape({ name: 'High Yield Savings' })).toBe('savings');
		expect(accountShape({ name: 'Ally HYS' })).toBe('savings');
		expect(accountShape({ name: 'Money Market' })).toBe('savings');
		expect(accountShape({ name: 'Rainy day', type: 'savings' })).toBe('savings');
	});
	it('cards, loans and investments are other', () => {
		expect(accountShape({ name: 'Freedom Visa' })).toBe('other');
		expect(accountShape({ name: 'Auto Loan' })).toBe('other');
		expect(accountShape({ name: 'Brokerage' })).toBe('other');
		expect(accountShape({ name: 'Anything', type: 'credit' })).toBe('other');
	});
	it('everything else is checking', () => {
		expect(accountShape({ name: 'Easy Checking' })).toBe('checking');
		expect(accountShape({ name: 'Spending Account' })).toBe('checking');
		expect(accountShape({ name: 'Wallet', type: 'cash' })).toBe('checking');
	});
});

describe('guessRoles', () => {
	it('their four accounts', () => {
		expect(guessRoles(theirs, members)).toEqual({
			us: { role: 'bills', ownerUserId: null },
			hys: { role: 'savings', ownerUserId: null },
			f1: { role: 'fun', ownerUserId: 'u-travis' },
			f2: { role: 'fun', ownerUserId: 'u-jess' }
		});
	});
	it('a member name in the account wins over the order', () => {
		const rows = theirs.map((r) =>
			r.key === 'f1' ? { ...r, name: 'Jess fun' } : r.key === 'f2' ? { ...r, name: 'Spending' } : r
		);
		const g = guessRoles(rows, members);
		expect(g.f1.ownerUserId).toBe('u-jess');
		expect(g.f2.ownerUserId).toBe('u-travis');
	});
	it('the busiest checking is bills; balance breaks a tie', () => {
		const rows: RoleRow[] = [
			{ key: 'a', name: 'Checking A', balanceCents: 100, posted: 5 },
			{ key: 'b', name: 'Checking B', balanceCents: 900, posted: 5 },
			{ key: 'c', name: 'Checking C', balanceCents: 50, posted: 2 }
		];
		const g = guessRoles(rows, members);
		expect(g.b.role).toBe('bills');
		expect(g.a.role).toBe('fun');
		expect(g.c.role).toBe('fun');
	});
	it('more fun accounts than people: the extra has no owner', () => {
		const rows: RoleRow[] = [
			{ key: 'a', name: 'Main', posted: 9 },
			{ key: 'b', name: 'Two' },
			{ key: 'c', name: 'Three' },
			{ key: 'd', name: 'Four' }
		];
		const g = guessRoles(rows, members);
		expect(g.d).toEqual({ role: 'fun', ownerUserId: null });
	});
	it('one checking only → bills; no accounts → nothing', () => {
		expect(guessRoles([{ key: 'x', name: 'Checking' }], members)).toEqual({
			x: { role: 'bills', ownerUserId: null }
		});
		expect(guessRoles([], members)).toEqual({});
	});
	it('keeps the roles the server already has', () => {
		const rows = theirs.map((r) => ({
			...r,
			role: r.key === 'f1' ? ('bills' as const) : ('other' as const),
			ownerUserId: null
		}));
		expect(rolesChosen(rows)).toBe(true);
		const g = guessRoles(rows, members);
		expect(g.f1.role).toBe('bills');
		expect(g.us.role).toBe('other');
		expect(rolesChosen(theirs)).toBe(false);
	});
});

describe('assignOwners', () => {
	it('whole first names only', () => {
		expect(
			assignOwners(
				[
					{ key: 'a', name: 'Jessup savings' },
					{ key: 'b', name: 'other' }
				],
				members
			)
		).toEqual({ a: 'u-travis', b: 'u-jess' });
	});
	it('no members → no owners', () => {
		expect(assignOwners([{ key: 'a', name: 'x' }], [])).toEqual({ a: null });
	});
});

describe('roleProblems', () => {
	it('only one bills account', () => {
		expect(
			roleProblems([
				{ role: 'bills', ownerUserId: null },
				{ role: 'bills', ownerUserId: null }
			])
		).toHaveLength(1);
		expect(roleProblems([{ role: 'bills', ownerUserId: null }])).toEqual([]);
		expect(roleProblems([])).toEqual([]);
	});
});
