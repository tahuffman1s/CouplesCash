import { fail } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type { Account, Category, Plan, RecurringItem } from '#lib/types.ts';
import { endpoints, isApiError } from '#lib/server/api.ts';
import { str } from '#lib/server/forms.ts';
import { quickAddAction } from '#lib/server/quickadd.ts';
import { addTransactionAction } from '#lib/server/addtransaction.ts';
import { isValidISODate } from '#lib/utils/dates.ts';

/**
 * v7 home: the Friday plan (`GET /plan?date=`), plus what the + sheet needs. When there is no bills
 * account yet the page shows the guided setup instead (PLAN-V7 "First run").
 */
export const load: PageServerLoad = async (event) => {
	const d = event.url.searchParams.get('date');
	const date = d && isValidISODate(d) ? d : null;
	const empty = {
		date,
		plan: null as Plan | null,
		categories: [] as Category[],
		accounts: [] as Account[],
		items: [] as RecurringItem[],
		joined: event.url.searchParams.has('joined')
	};
	if (!event.locals.household) return empty;
	const v = endpoints(event);
	const quiet = <T>(p: Promise<T>, fallback: T) => p.catch(() => fallback);
	const [plan, categories, accounts, items] = await Promise.all([
		quiet(v.plan.get(date), null as Plan | null),
		quiet(v.categories(), [] as Category[]),
		quiet(v.accounts.list(), [] as Account[]),
		quiet(v.recurring.list(), [] as RecurringItem[])
	]);
	return { ...empty, plan, categories, accounts, items };
};

export const actions: Actions = {
	quickAdd: quickAddAction,
	addTransaction: addTransactionAction,
	/** Tick / untick a planned line (a bill, a fun-money transfer, the paycheck). */
	paid: async (event) => {
		const data = await event.request.formData();
		const itemId = str(data, 'itemId');
		const dueDate = str(data, 'dueDate');
		if (!itemId || !isValidISODate(dueDate)) return fail(400, { planError: 'Pick a line.' });
		const v = endpoints(event);
		try {
			if (data.get('paid') === '1') await v.recurring.markPaid(itemId, dueDate);
			else await v.recurring.markUnpaid(itemId, dueDate);
		} catch (e) {
			return fail(isApiError(e) ? e.status : 500, {
				planError: isApiError(e) ? e.message : 'Could not update. Try again.'
			});
		}
		return { ticked: data.get('paid') === '1' ? 'paid' : 'unpaid' };
	},
	/** "Move to savings" → Done / Undo (`POST|DELETE /plan/sweep/done`). */
	sweep: async (event) => {
		const data = await event.request.formData();
		const weekStart = str(data, 'weekStart');
		if (!isValidISODate(weekStart)) return fail(400, { planError: 'Pick the week.' });
		const v = endpoints(event);
		try {
			if (data.get('done') === '1') await v.plan.sweepDone(weekStart);
			else await v.plan.sweepUndo(weekStart);
		} catch (e) {
			return fail(isApiError(e) ? e.status : 500, {
				planError: isApiError(e) ? e.message : 'Could not update. Try again.'
			});
		}
		return { swept: data.get('done') === '1' };
	}
};
