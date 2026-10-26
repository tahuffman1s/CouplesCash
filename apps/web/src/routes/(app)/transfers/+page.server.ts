import type { PageServerLoad } from './$types';
import type { Category, TransferReport } from '#lib/types.ts';
import { api, endpoints } from '#lib/server/api.ts';
import { today } from '#lib/utils/dates.ts';

export const load: PageServerLoad = async (event) => {
	const m = event.url.searchParams.get('month') ?? event.url.searchParams.get('m');
	const month = m && /^\d{4}-(0[1-9]|1[0-2])$/.test(m) ? m : today().slice(0, 7);
	if (!event.locals.household)
		return { month, report: null as TransferReport | null, categories: [] as Category[] };
	const [report, categories] = await Promise.all([
		endpoints(event)
			.transfers.report(month)
			.catch(() => null as TransferReport | null),
		api(event)
			.get<Category[]>('/categories')
			.catch(() => [] as Category[])
	]);
	return { month, report, categories };
};
