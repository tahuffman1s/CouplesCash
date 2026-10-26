/**
 * v7 Bills list (pure, unit-tested): items grouped by when they're next due, measured in pay weeks
 * (payday → the day before the next payday): "This pay week", "Next 4 weeks", "Later"; one-offs
 * that already happened go under "Earlier", items that stopped under "Ended".
 */
import type { Account, Kind, RecurringItem } from '#lib/types.ts';
import { addDays, type ISODate } from './dates.ts';
import { accountLabel, payWeekStart } from './plan.ts';

export type BillFilter = Kind | 'all' | 'once';
export type BillGroupKey = 'week' | 'next' | 'later' | 'earlier' | 'ended';
export const BILL_GROUP_LABEL: Record<BillGroupKey, string> = {
	week: 'This pay week',
	next: 'Next 4 weeks',
	later: 'Later',
	earlier: 'Earlier',
	ended: 'Ended'
};
export const BILL_FILTERS: readonly { value: BillFilter; label: string }[] = [
	{ value: 'all', label: 'All' },
	{ value: 'bill', label: 'Bills' },
	{ value: 'subscription', label: 'Subscriptions' },
	{ value: 'income', label: 'Income' },
	{ value: 'transfer', label: 'Transfers' },
	{ value: 'once', label: 'One-offs' }
];

/** The date a row is sorted and grouped by: next due, or a one-off's own date. */
export const billDate = (i: Pick<RecurringItem, 'nextDueDate' | 'anchorDate' | 'frequency'>) =>
	i.nextDueDate ?? (i.frequency === 'once' ? i.anchorDate : null);

export function matchesFilter(i: Pick<RecurringItem, 'kind' | 'frequency'>, f: BillFilter) {
	if (f === 'all') return true;
	if (f === 'once') return i.frequency === 'once';
	return i.kind === f && i.frequency !== 'once';
}

export function billGroup(
	i: Pick<RecurringItem, 'nextDueDate' | 'anchorDate' | 'frequency'>,
	today: ISODate,
	paydayWeekday: number
): BillGroupKey {
	const d = billDate(i);
	if (!d) return 'ended';
	const end = addDays(payWeekStart(today, paydayWeekday), 6);
	if (d < today) return 'earlier';
	if (d <= end) return 'week';
	if (d <= addDays(end, 28)) return 'next';
	return 'later';
}

export function groupBills(
	items: RecurringItem[],
	opts: { today: ISODate; paydayWeekday: number; filter: BillFilter }
): { key: BillGroupKey; label: string; items: RecurringItem[] }[] {
	const order: BillGroupKey[] = ['week', 'next', 'later', 'earlier', 'ended'];
	const groups = new Map<BillGroupKey, RecurringItem[]>();
	for (const i of items) {
		if (!matchesFilter(i, opts.filter)) continue;
		const g = billGroup(i, opts.today, opts.paydayWeekday);
		// past one-offs only under the One-offs chip
		if (g === 'earlier' && opts.filter !== 'once') continue;
		groups.set(g, [...(groups.get(g) ?? []), i]);
	}
	return order
		.filter((k) => groups.has(k))
		.map((k) => ({
			key: k,
			label: BILL_GROUP_LABEL[k],
			items: groups
				.get(k)!
				.toSorted(
					(a, b) =>
						(k === 'earlier' ? -1 : 1) *
							(billDate(a) ?? '9999').localeCompare(billDate(b) ?? '9999') ||
						a.name.localeCompare(b.name)
				)
		}));
}

/** "Bills" / "Savings" / "Fun (Travis)" / the account's name, or null for any account. */
export function billAccountLabel(accountId: string | null | undefined, accounts: Account[]) {
	const a = accountId ? accounts.find((x) => x.id === accountId) : undefined;
	if (!a) return null;
	if (a.role === 'bills') return 'Bills';
	return accountLabel({ name: a.name, role: a.role ?? 'other', ownerName: a.ownerName ?? null });
}

/** "US Bank → Fun (Travis)" for a transfer item. */
export function transferRoute(
	i: Pick<RecurringItem, 'fromAccountId' | 'toAccountId' | 'fromAccountName' | 'toAccountName'>,
	accounts: Account[]
): string {
	const name = (id: string | null | undefined, fallback: string | null | undefined) => {
		const a = id ? accounts.find((x) => x.id === id) : undefined;
		if (a)
			return a.role === 'bills'
				? a.name
				: accountLabel({ name: a.name, role: a.role ?? 'other', ownerName: a.ownerName ?? null });
		return fallback ?? 'another account';
	};
	return `${name(i.fromAccountId, i.fromAccountName)} → ${name(i.toAccountId, i.toAccountName)}`;
}
