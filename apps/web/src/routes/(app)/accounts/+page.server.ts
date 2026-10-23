import type { PageServerLoad } from './$types';
import type { Account, Goal } from '#lib/types.ts';
import { api, endpoints } from '#lib/server/api.ts';

export const load: PageServerLoad = async (event) => {
	if (!event.locals.household) return { accounts: null, goals: [] as Goal[] };
	const [accounts, goals] = await Promise.all([
		api(event).get<Account[]>('/accounts'),
		endpoints(event)
			.goals.list()
			.catch(() => [] as Goal[])
	]);
	return { accounts, goals };
};
