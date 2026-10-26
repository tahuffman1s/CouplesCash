import type { PageServerLoad } from './$types';
import type { Account, Category, Goal, PendingView, TransferReport } from '#lib/types.ts';
import { api, endpoints } from '#lib/server/api.ts';

/** v6 Money: every account, pending by account, goals, money moved between accounts. */
export const load: PageServerLoad = async (event) => {
	const empty = {
		accounts: null as Account[] | null,
		goals: [] as Goal[],
		pending: null as PendingView | null,
		transfers: null as TransferReport | null,
		categories: [] as Category[]
	};
	if (!event.locals.household) return empty;
	const v = endpoints(event);
	const quiet = <T>(p: Promise<T>, fallback: T) => p.catch(() => fallback);
	const [accounts, goals, pending, transfers, categories] = await Promise.all([
		api(event).get<Account[]>('/accounts'),
		quiet(v.goals.list(), [] as Goal[]),
		quiet(v.pending.view(), null as PendingView | null),
		quiet(v.transfers.report(), null as TransferReport | null),
		quiet(api(event).get<Category[]>('/categories'), [] as Category[])
	]);
	return { accounts, goals, pending, transfers, categories };
};
