import { redirect } from '@sveltejs/kit';
import type { PageServerLoad } from './$types';
import type { Account, CsvImportRecord } from '#lib/types.ts';
import { api, endpoints } from '#lib/server/api.ts';

/**
 * The file is parsed in the browser (papaparse) and the mapped rows are posted through the proxy,
 * so this load only needs the accounts to pick from and the recent imports.
 */
export const load: PageServerLoad = async (event) => {
	if (!event.locals.household) redirect(303, '/couple');
	const [accounts, recent] = await Promise.all([
		api(event)
			.get<Account[]>('/accounts')
			.catch(() => [] as Account[]),
		endpoints(event)
			.imports.listCsv()
			.catch(() => [] as CsvImportRecord[])
	]);
	return { accounts, recent };
};
