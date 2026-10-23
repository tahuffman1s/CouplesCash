import type { LayoutServerLoad } from './$types';
import { endpoints } from '#lib/server/api.ts';

/**
 * Household settings (currency, week start, pay period…) for every app page. One extra GET per
 * navigation; it re-runs whenever a page calls `invalidateAll()` after a mutation.
 */
export const load: LayoutServerLoad = async (event) => {
	if (!event.locals.household) return { household: null };
	try {
		return { household: await endpoints(event).household.get() };
	} catch {
		return { household: null };
	}
};
