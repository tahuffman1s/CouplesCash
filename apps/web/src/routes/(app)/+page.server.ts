import { fail } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type { ActivityEvent, Category, PayPeriodView, Summary, Todo } from '#lib/types.ts';
import { api, endpoints, isApiError } from '#lib/server/api.ts';
import { echo, parseExpenseForm } from '#lib/server/forms.ts';

const empty = {
	summary: null as Summary | null,
	payperiod: null as PayPeriodView | null,
	todos: [] as Todo[],
	activity: [] as ActivityEvent[],
	categories: [] as Category[]
};

export const load: PageServerLoad = async (event) => {
	if (!event.locals.household) return empty;
	const client = api(event);
	const v2 = endpoints(event);
	// The summary is essential; the v2 widgets degrade to "hidden" if their call fails.
	const quiet = <T>(p: Promise<T>, fallback: T) => p.catch(() => fallback);
	const [summary, payperiod, todos, activity, categories] = await Promise.all([
		client.get<Summary>('/summary'),
		quiet(v2.payperiod.view(), null),
		quiet(v2.todos.list(false), [] as Todo[]),
		quiet(
			v2.activity.list({ limit: 5 }).then((p) => p.events),
			[] as ActivityEvent[]
		),
		quiet(client.get<Category[]>('/categories'), [] as Category[])
	]);
	return { summary, payperiod, todos, activity, categories };
};

export const actions: Actions = {
	/** Quick-add sheet: name, amount, category (date = today, household default account). */
	quickExpense: async (event) => {
		const data = await event.request.formData();
		const parsed = parseExpenseForm(data);
		if (!parsed.ok) return fail(400, { error: parsed.error, values: echo(data) });
		try {
			const expense = await endpoints(event).expenses.create(parsed.value);
			return { expenseAdded: expense.name };
		} catch (e) {
			const message = isApiError(e) ? e.message : 'Could not save. Try again.';
			return fail(isApiError(e) ? e.status : 500, { error: message, values: echo(data) });
		}
	}
};
