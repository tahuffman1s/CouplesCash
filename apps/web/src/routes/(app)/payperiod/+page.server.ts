import type { PageServerLoad } from './$types';
import { endpoints } from '#lib/server/api.ts';
import { isValidISODate, today } from '#lib/utils/dates.ts';

export const load: PageServerLoad = async (event) => {
	const d = event.url.searchParams.get('date');
	const date = d && isValidISODate(d) ? d : today();
	if (!event.locals.household) return { date, view: null, configured: false };
	const view = await endpoints(event).payperiod.view(date);
	return { date, view, configured: view !== null };
};
