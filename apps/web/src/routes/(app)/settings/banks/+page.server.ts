import { fail, redirect } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type { Account } from '#lib/types.ts';
import { api, endpoints, isApiError } from '#lib/server/api.ts';
import { echo, str } from '#lib/server/forms.ts';

export const load: PageServerLoad = async (event) => {
	if (!event.locals.household) redirect(303, '/couple');
	const [links, accounts, plaid] = await Promise.all([
		endpoints(event).banks.list(),
		api(event)
			.get<Account[]>('/accounts')
			.catch(() => [] as Account[]),
		endpoints(event).banks.plaidStatus()
	]);
	// links === null → the API answered 503 bank_sync_not_configured
	return { links, accounts, plaid, notConfigured: links === null };
};

export const actions: Actions = {
	claim: async (event) => {
		const data = await event.request.formData();
		const setupToken = str(data, 'setupToken').replace(/\s+/g, '');
		const name = str(data, 'name') || 'My bank';
		if (!setupToken)
			return fail(400, { error: 'Paste the setup token first.', values: echo(data) });
		if (name.length > 40) return fail(400, { error: 'Keep the name short.', values: echo(data) });
		try {
			const link = await endpoints(event).banks.claim(setupToken, name);
			return { linked: link.name, accounts: link.accounts.length };
		} catch (e) {
			if (isApiError(e, 503)) return fail(503, { notConfigured: true, values: echo(data) });
			if (isApiError(e, 400))
				return fail(400, {
					error:
						e.field('setupToken') ??
						e.message ??
						'That setup token is invalid or has expired. Create a new one at the Bridge.',
					values: echo(data)
				});
			if (isApiError(e, 502))
				return fail(502, {
					error: 'Could not reach SimpleFIN Bridge. Try again in a minute.',
					values: echo(data)
				});
			return fail(isApiError(e) ? e.status : 500, {
				error: isApiError(e) ? e.message : 'Could not link the bank.',
				values: echo(data)
			});
		}
	},
	rename: async (event) => {
		const data = await event.request.formData();
		const id = str(data, 'id');
		const name = str(data, 'name');
		if (!name || name.length > 40) return fail(400, { error: 'Give the link a short name.' });
		try {
			await endpoints(event).banks.update(id, { name });
			return { renamed: name };
		} catch (e) {
			return fail(isApiError(e) ? e.status : 500, { error: 'Could not rename the link.' });
		}
	},
	toggle: async (event) => {
		const data = await event.request.formData();
		const id = str(data, 'id');
		const syncEnabled = data.get('syncEnabled') === 'on';
		try {
			await endpoints(event).banks.update(id, { syncEnabled });
			return { toggled: syncEnabled ? 'resumed' : 'paused' };
		} catch (e) {
			return fail(isApiError(e) ? e.status : 500, { error: 'Could not change syncing.' });
		}
	},
	unlink: async (event) => {
		const data = await event.request.formData();
		const id = str(data, 'id');
		try {
			await endpoints(event).banks.remove(id);
			return { unlinked: true };
		} catch (e) {
			if (!isApiError(e, 404)) return fail(500, { error: 'Could not unlink the bank.' });
			return { unlinked: true };
		}
	}
};
