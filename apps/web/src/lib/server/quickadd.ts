import { fail, type RequestEvent } from '@sveltejs/kit';
import type { Category, Kind } from '#lib/types.ts';
import { KINDS } from '#lib/types.ts';
import { api, endpoints, isApiError } from './api';
import { optional, str } from './forms';
import { isValidISODate, today } from '#lib/utils/dates.ts';
import {
	draftProblems,
	isCadence,
	parseQuickAdd,
	scheduleText,
	toRecurringBodies,
	type QuickAddDraft
} from '#lib/utils/quickadd.ts';

export type QuickAddResult = {
	quickAdded: { name: string; schedule: string; amountCents: number; kind: Kind; count: number };
};

/**
 * v6 quick add (`?/quickAdd` on Week, Bills and Transactions). With JavaScript the sheet posts the
 * corrected preview (`structured=1` + one field per chip); without it, the server parses the
 * sentence itself with the same `quickadd.ts`, so the one-field form works either way.
 */
export async function quickAddAction(event: RequestEvent) {
	const data = await event.request.formData();
	const text = str(data, 'text');
	const todayIso = today();
	let draft: QuickAddDraft;
	let categoryId: string | null;
	let accountId: string | null;
	if (data.get('structured') === '1') {
		const cents = Number(str(data, 'amountCents'));
		const kind = str(data, 'kind') as Kind;
		const cadence = str(data, 'cadence');
		const anchorDate = str(data, 'anchorDate');
		if (!KINDS.includes(kind) || !isCadence(cadence) || !isValidISODate(anchorDate))
			return fail(400, { quickError: 'Check the kind, how often and the date.', text });
		const name = str(data, 'name').slice(0, 80);
		draft = {
			...parseQuickAdd(text, todayIso),
			name,
			kind,
			amountCents: Number.isInteger(cents) && cents > 0 ? cents : null,
			amountVaries: data.get('amountVaries') === 'on',
			cadence,
			anchorDate,
			autoPay: data.get('autoPay') === 'on',
			bankName: str(data, 'bankName') || name
		};
		categoryId = optional(data, 'categoryId');
		accountId = optional(data, 'accountId');
	} else {
		draft = parseQuickAdd(text, todayIso);
		const [categories, household] = await Promise.all([
			api(event)
				.get<Category[]>('/categories')
				.catch(() => [] as Category[]),
			endpoints(event)
				.household.get()
				.catch(() => null)
		]);
		const wanted = draft.category?.toLowerCase();
		categoryId = categories.find((c) => c.name.toLowerCase() === wanted)?.id ?? null;
		accountId = household?.defaultAccountId ?? null;
	}
	const problems = draftProblems(draft);
	if (problems.length) return fail(400, { quickError: `${problems.join('. ')}.`, text });
	const bodies = toRecurringBodies(draft, { categoryId, accountId });
	try {
		for (const body of bodies) await endpoints(event).recurring.create(body);
	} catch (e) {
		return fail(isApiError(e) ? e.status : 500, {
			quickError: isApiError(e) ? e.message : 'Could not save. Try again.',
			text
		});
	}
	return {
		quickAdded: {
			name: draft.name,
			schedule: scheduleText(draft, todayIso),
			amountCents: draft.amountCents ?? 0,
			kind: draft.kind,
			count: bodies.length
		}
	} satisfies QuickAddResult;
}
