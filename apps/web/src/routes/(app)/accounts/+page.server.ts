import type { PageServerLoad } from './$types';
import type { Account } from '#lib/types.ts';
import { api } from '#lib/server/api.ts';

export const load: PageServerLoad = async (event) => {
	if (!event.locals.household) return { accounts: null };
	const accounts = await api(event).get<Account[]>('/accounts');
	return { accounts };
};
