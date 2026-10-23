import type { PageServerLoad } from './$types';
import { endpoints } from '#lib/server/api.ts';

/** The More menu shows how many bank/CSV transactions wait in the inbox (0 when unavailable). */
export const load: PageServerLoad = async (event) => {
	if (!event.locals.household) return { inboxPending: 0 };
	try {
		const summary = await endpoints(event).inbox.summary();
		return { inboxPending: summary.pending };
	} catch {
		return { inboxPending: 0 };
	}
};
