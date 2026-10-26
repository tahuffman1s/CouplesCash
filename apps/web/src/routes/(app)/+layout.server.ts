import type { LayoutServerLoad } from './$types';
import type { Household, InboxSummary } from '#lib/types.ts';
import { endpoints } from '#lib/server/api.ts';

/**
 * Household settings (currency, week start, pay period…) and the To-review summary (the
 * Transactions tab badge) for every app page. Two small GETs per navigation, in parallel; both
 * re-run whenever a page calls `invalidateAll()` after a mutation.
 */
export const load: LayoutServerLoad = async (event) => {
	if (!event.locals.household)
		return { household: null as Household | null, inbox: null as InboxSummary | null };
	const v = endpoints(event);
	const [household, inbox] = await Promise.all([
		v.household.get().catch(() => null as Household | null),
		v.inbox.summary().catch(() => null as InboxSummary | null)
	]);
	return { household, inbox };
};
