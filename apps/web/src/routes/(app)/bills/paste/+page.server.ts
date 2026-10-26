import { redirect } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type { Account, Category } from '#lib/types.ts';
import { endpoints } from '#lib/server/api.ts';
import { saveNotes } from '#lib/server/setup.ts';

/** v7 Bills → "Paste from notes": the same importer as setup step 5. */
export const load: PageServerLoad = async (event) => {
	if (!event.locals.household) redirect(303, '/couple');
	const v = endpoints(event);
	const [categories, accounts] = await Promise.all([
		v.categories().catch(() => [] as Category[]),
		v.accounts.list().catch(() => [] as Account[])
	]);
	return {
		categories,
		billsAccountId: accounts.find((a) => a.role === 'bills')?.id ?? null,
		savingsAccountId: accounts.find((a) => a.role === 'savings')?.id ?? null
	};
};

export const actions: Actions = {
	default: async (event) => {
		const r = await saveNotes(event, await event.request.formData());
		if (!('ok' in r)) return r;
		redirect(303, `/bills?added=${r.added}`);
	}
};
