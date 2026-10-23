import type { PageServerLoad } from './$types';
import type { Category, InboxPage, InboxStatus, InboxSummary } from '#lib/types.ts';
import { INBOX_STATUSES } from '#lib/types.ts';
import { api, endpoints } from '#lib/server/api.ts';

export const load: PageServerLoad = async (event) => {
	const raw = event.url.searchParams.get('status');
	const status: InboxStatus = INBOX_STATUSES.includes(raw as InboxStatus)
		? (raw as InboxStatus)
		: 'pending';
	if (!event.locals.household)
		return { status, summary: null, page: null, categories: [] as Category[] };
	const v3 = endpoints(event);
	const quiet = <T>(p: Promise<T>, fallback: T) => p.catch(() => fallback);
	const [summary, page, categories] = await Promise.all([
		quiet(v3.inbox.summary(), null as InboxSummary | null),
		quiet(v3.inbox.list({ status, limit: 50 }), { items: [], nextBefore: null } as InboxPage),
		quiet(api(event).get<Category[]>('/categories'), [] as Category[])
	]);
	return { status, summary, page, categories };
};
