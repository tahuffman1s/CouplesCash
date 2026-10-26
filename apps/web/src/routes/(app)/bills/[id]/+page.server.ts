import { error, fail, redirect } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import type {
	Account,
	BillPayment,
	CalendarResponse,
	Category,
	RecurringItem
} from '#lib/types.ts';
import { api, endpoints, isApiError } from '#lib/server/api.ts';
import { echo, parseRecurringForm } from '#lib/server/forms.ts';
import { addDays, today } from '#lib/utils/dates.ts';

export const load: PageServerLoad = async (event) => {
	if (!event.locals.household) redirect(303, '/couple');
	const client = api(event);
	try {
		const from = addDays(today(), -31);
		const to = addDays(today(), 61);
		const [item, categories, accounts, calendar, payments] = await Promise.all([
			client.get<RecurringItem>(`/recurring/${event.params.id}`),
			client.get<Category[]>('/categories'),
			client.get<Account[]>('/accounts'),
			client.get<CalendarResponse>(`/calendar?from=${from}&to=${to}`),
			// v5: Payments (older API builds → null, section hidden)
			endpoints(event)
				.recurring.payments(event.params.id, 12)
				.catch(() => null as BillPayment[] | null)
		]);
		return {
			item,
			categories,
			accounts,
			occurrences: calendar.occurrences.filter((o) => o.itemId === item.id),
			payments,
			created: event.url.searchParams.has('created')
		};
	} catch (e) {
		if (isApiError(e, 404)) error(404, 'Item not found');
		throw e;
	}
};

export const actions: Actions = {
	update: async (event) => {
		const data = await event.request.formData();
		const parsed = parseRecurringForm(data);
		if (!parsed.ok) return fail(400, { error: parsed.error, values: echo(data) });
		try {
			await api(event).put<RecurringItem>(`/recurring/${event.params.id}`, parsed.value);
		} catch (e) {
			const message = isApiError(e) ? e.message : 'Could not save. Try again.';
			return fail(isApiError(e) ? e.status : 500, { error: message, values: echo(data) });
		}
		return { saved: true };
	},
	delete: async (event) => {
		try {
			await api(event).del(`/recurring/${event.params.id}`);
		} catch (e) {
			if (!isApiError(e, 404)) return fail(500, { error: 'Could not remove this item.' });
		}
		redirect(303, '/bills');
	}
};
