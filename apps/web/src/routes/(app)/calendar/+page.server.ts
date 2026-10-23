import type { PageServerLoad } from './$types';
import type { CalendarResponse } from '#lib/types.ts';
import { api } from '#lib/server/api.ts';
import { endOfMonth, startOfMonth, today } from '#lib/utils/dates.ts';

export const load: PageServerLoad = async (event) => {
	const m = event.url.searchParams.get('m');
	const month = m && /^\d{4}-(0[1-9]|1[0-2])$/.test(m) ? m : today().slice(0, 7);
	if (!event.locals.household) return { month, occurrences: null };
	const from = startOfMonth(`${month}-01`);
	const to = endOfMonth(`${month}-01`);
	const res = await api(event).get<CalendarResponse>(`/calendar?from=${from}&to=${to}`);
	return { month, occurrences: res.occurrences };
};
