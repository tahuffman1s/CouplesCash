import type { PageServerLoad } from './$types';
import { endpoints } from '#lib/server/api.ts';

export const load: PageServerLoad = async (event) => {
	if (!event.locals.household) return { goals: null };
	const goals = await endpoints(event).goals.list();
	return { goals };
};
