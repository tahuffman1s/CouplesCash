import { redirect } from '@sveltejs/kit';
import type { PageServerLoad } from './$types';
import { endpoints } from '#lib/server/api.ts';
import { today } from '#lib/utils/dates.ts';

export const load: PageServerLoad = async (event) => {
	if (!event.locals.household) redirect(303, '/couple');
	const m = event.url.searchParams.get('m');
	const month = m && /^\d{4}-(0[1-9]|1[0-2])$/.test(m) ? m : today().slice(0, 7);
	const report = await endpoints(event).reports.month(month);
	return { month, report };
};
