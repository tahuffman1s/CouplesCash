import type { Actions, PageServerLoad } from './$types';
import type { Account, Category, InboxItem, InboxPage, TransactionFeed } from '#lib/types.ts';
import { api, endpoints } from '#lib/server/api.ts';
import { quickAddAction } from '#lib/server/quickadd.ts';
import { addTransactionAction } from '#lib/server/addtransaction.ts';

/**
 * v6 Transactions tab: everything that hit your accounts. To review on top (the complete pending
 * inbox, so nothing that needs a person is paged away), then the feed's Pending and posted rows.
 */
export const load: PageServerLoad = async (event) => {
	const accountId = event.url.searchParams.get('account') || null;
	const q = (event.url.searchParams.get('q') ?? '').trim().slice(0, 100) || null;
	const empty = {
		accountId,
		q,
		// v7: fun-money accounts are personal; hidden unless asked for
		showFun: event.url.searchParams.get('fun') === '1',
		added: event.url.searchParams.has('added'),
		feed: null as TransactionFeed | null,
		review: [] as InboxItem[],
		categories: [] as Category[],
		accounts: [] as Account[]
	};
	if (!event.locals.household) return empty;
	const v = endpoints(event);
	const quiet = <T>(p: Promise<T>, fallback: T) => p.catch(() => fallback);
	const [feed, review, categories, accounts] = await Promise.all([
		quiet(v.transactions.feed({ accountId, q, limit: 50 }), null as TransactionFeed | null),
		quiet(v.inbox.list({ status: 'pending', limit: 100 }), {
			items: [],
			nextBefore: null
		} as InboxPage),
		quiet(api(event).get<Category[]>('/categories'), [] as Category[]),
		quiet(api(event).get<Account[]>('/accounts'), [] as Account[])
	]);
	return { ...empty, feed, review: review.items, categories, accounts };
};

export const actions: Actions = {
	quickAdd: quickAddAction,
	addTransaction: addTransactionAction
};
