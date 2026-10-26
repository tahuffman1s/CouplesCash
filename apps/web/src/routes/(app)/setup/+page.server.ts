import { redirect } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import {
	claimBank,
	funRows,
	paycheckGuess,
	roleDefaults,
	roleRows,
	saveFun,
	saveNotes,
	savePayday,
	saveRoles,
	setupData
} from '#lib/server/setup.ts';
import { endpoints } from '#lib/server/api.ts';
import {
	firstOpenStep,
	isSetupStep,
	nextStep,
	stepsDone,
	type SetupStep
} from '#lib/utils/setup.ts';

/**
 * v7 guided setup (PLAN-V7 "First run"): six steps, each skippable and reachable on its own
 * (`?step=`), each saving through the same helpers Settings uses.
 */
export const load: PageServerLoad = async (event) => {
	if (!event.locals.household) redirect(303, '/couple');
	const data = await setupData(event);
	const household = await endpoints(event)
		.household.get()
		.catch(() => null);
	const funSet = data.items.some((i) => i.kind === 'transfer');
	const done = stepsDone(data.plan?.setup, { funSet });
	const s = event.url.searchParams.get('step');
	const step: SetupStep = isSetupStep(s) ? s : (firstOpenStep(done) ?? 'banks');
	const rows = roleRows(data.accounts, data.banks);
	return {
		...data,
		step,
		done,
		rows,
		defaults: roleDefaults(rows, household),
		funRows: funRows(data.accounts, data.items),
		paycheck: step === 'payday' ? await paycheckGuess(event, data.items, data.plan) : null,
		added: Number(event.url.searchParams.get('added') ?? 0) || 0,
		linked: event.url.searchParams.get('linked')
			? {
					name: (event.url.searchParams.get('linked') ?? '').slice(0, 40),
					found: Number(event.url.searchParams.get('found') ?? 0) || 0
				}
			: null
	};
};

const go = (step: SetupStep, extra = '') => {
	const next = nextStep(step);
	redirect(303, next ? `/setup?step=${next}${extra}` : '/week');
};

export const actions: Actions = {
	claim: async (event) => {
		const r = await claimBank(event, await event.request.formData());
		if (!('ok' in r)) return r;
		// linked: straight on to "which account is which"
		redirect(303, `/setup?step=roles&linked=${encodeURIComponent(r.linked)}&found=${r.found}`);
	},
	roles: async (event) => {
		const r = await saveRoles(event, await event.request.formData());
		if (!('ok' in r)) return r;
		go('roles');
	},
	payday: async (event) => {
		const r = await savePayday(event, await event.request.formData());
		if (!('ok' in r)) return r;
		go('payday');
	},
	fun: async (event) => {
		const r = await saveFun(event, await event.request.formData());
		if (!('ok' in r)) return r;
		go('fun');
	},
	notes: async (event) => {
		const r = await saveNotes(event, await event.request.formData());
		if (!('ok' in r)) return r;
		go('notes', `&added=${r.added}`);
	}
};
