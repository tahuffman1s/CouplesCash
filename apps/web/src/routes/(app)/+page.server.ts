import type { PageServerLoad } from './$types';
import type { Summary } from '#lib/types.ts';
import { api } from '#lib/server/api.ts';

export const load: PageServerLoad = async (event) => {
	if (!event.locals.household) return { summary: null };
	const summary = await api(event).get<Summary>('/summary');
	return { summary };
};
