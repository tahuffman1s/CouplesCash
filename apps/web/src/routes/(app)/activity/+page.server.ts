import type { PageServerLoad } from './$types';
import { endpoints } from '#lib/server/api.ts';

export const load: PageServerLoad = async (event) => {
	if (!event.locals.household) return { page: null };
	const page = await endpoints(event).activity.list({ limit: 30 });
	return { page };
};
