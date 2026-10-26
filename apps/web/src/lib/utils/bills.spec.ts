import { describe, expect, it } from 'vitest';
import type { Account, RecurringItem } from '#lib/types.ts';
import { billAccountLabel, billGroup, groupBills, matchesFilter, transferRoute } from './bills.ts';

const TODAY = '2026-10-26'; // Monday; Friday pay week Oct 23 – Oct 29
const item = (over: Partial<RecurringItem>): RecurringItem =>
	({
		id: over.name ?? 'x',
		name: 'X',
		kind: 'bill',
		frequency: 'monthly',
		anchorDate: '2026-10-01',
		nextDueDate: '2026-10-27',
		...over
	}) as RecurringItem;

describe('billGroup', () => {
	it('this pay week, next 4 weeks, later', () => {
		expect(billGroup(item({ nextDueDate: '2026-10-26' }), TODAY, 5)).toBe('week');
		expect(billGroup(item({ nextDueDate: '2026-10-29' }), TODAY, 5)).toBe('week');
		expect(billGroup(item({ nextDueDate: '2026-10-30' }), TODAY, 5)).toBe('next');
		expect(billGroup(item({ nextDueDate: '2026-11-26' }), TODAY, 5)).toBe('next');
		expect(billGroup(item({ nextDueDate: '2026-11-27' }), TODAY, 5)).toBe('later');
	});
	it('follows the payday', () => {
		// Monday payday: the week is Oct 26 – Nov 1
		expect(billGroup(item({ nextDueDate: '2026-10-31' }), TODAY, 1)).toBe('week');
		expect(billGroup(item({ nextDueDate: '2026-10-31' }), TODAY, 5)).toBe('next');
	});
	it('past one-offs and ended items', () => {
		expect(
			billGroup(item({ frequency: 'once', nextDueDate: null, anchorDate: '2026-10-20' }), TODAY, 5)
		).toBe('earlier');
		expect(billGroup(item({ nextDueDate: null }), TODAY, 5)).toBe('ended');
	});
});

describe('groupBills', () => {
	const items = [
		item({ name: 'Rent', nextDueDate: '2026-11-01' }),
		item({ name: 'Water', nextDueDate: '2026-10-28' }),
		item({ name: 'Netflix', kind: 'subscription', nextDueDate: '2026-10-27' }),
		item({ name: 'Paycheck', kind: 'income', frequency: 'weekly', nextDueDate: '2026-10-30' }),
		item({ name: 'Fun', kind: 'transfer', frequency: 'weekly', nextDueDate: '2026-10-30' }),
		item({ name: 'Car ins', frequency: 'yearly', nextDueDate: '2027-04-01' }),
		item({ name: 'Oil', frequency: 'once', anchorDate: '2026-10-20', nextDueDate: null }),
		item({ name: 'Gift', frequency: 'once', anchorDate: '2026-10-28', nextDueDate: '2026-10-28' })
	];
	it('all: grouped and sorted by date; past one-offs left out', () => {
		const g = groupBills(items, { today: TODAY, paydayWeekday: 5, filter: 'all' });
		expect(g.map((x) => [x.label, x.items.map((i) => i.name)])).toEqual([
			['This pay week', ['Netflix', 'Gift', 'Water']],
			['Next 4 weeks', ['Fun', 'Paycheck', 'Rent']],
			['Later', ['Car ins']]
		]);
	});
	it('chips', () => {
		const names = (filter: Parameters<typeof groupBills>[1]['filter']) =>
			groupBills(items, { today: TODAY, paydayWeekday: 5, filter }).flatMap((x) =>
				x.items.map((i) => i.name)
			);
		expect(names('bill')).toEqual(['Water', 'Rent', 'Car ins']);
		expect(names('subscription')).toEqual(['Netflix']);
		expect(names('income')).toEqual(['Paycheck']);
		expect(names('transfer')).toEqual(['Fun']);
		expect(names('once')).toEqual(['Gift', 'Oil']);
		expect(matchesFilter(item({ frequency: 'once' }), 'bill')).toBe(false);
	});
});

describe('labels', () => {
	const accounts = [
		{ id: 'b', name: 'U.S. Bank', role: 'bills', ownerName: null },
		{ id: 's', name: 'Online Savings', role: 'savings', ownerName: null },
		{ id: 'f', name: 'Spending', role: 'fun', ownerName: 'Travis H' },
		{ id: 'o', name: 'Visa', role: 'other', ownerName: null }
	] as Account[];
	it('account chip', () => {
		expect(billAccountLabel('b', accounts)).toBe('Bills');
		expect(billAccountLabel('s', accounts)).toBe('Savings');
		expect(billAccountLabel('f', accounts)).toBe('Fun (Travis)');
		expect(billAccountLabel('o', accounts)).toBe('Visa');
		expect(billAccountLabel(null, accounts)).toBeNull();
		expect(billAccountLabel('zzz', accounts)).toBeNull();
	});
	it('transfer route', () => {
		expect(transferRoute({ fromAccountId: 'b', toAccountId: 'f' }, accounts)).toBe(
			'U.S. Bank → Fun (Travis)'
		);
		expect(
			transferRoute(
				{ fromAccountId: 'gone', toAccountId: null, fromAccountName: 'Old', toAccountName: null },
				accounts
			)
		).toBe('Old → another account');
	});
});
