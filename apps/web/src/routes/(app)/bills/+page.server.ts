import type { PageServerLoad } from './$types';
import type { Category, RecurringItem } from '#lib/types.ts';
import { api } from '#lib/server/api.ts';

export const load: PageServerLoad = async (event) => {
	if (!event.locals.household) return { items: null, categories: [] as Category[] };
	const client = api(event);
	const [items, categories] = await Promise.all([
		client.get<RecurringItem[]>('/recurring'),
		client.get<Category[]>('/categories')
	]);
	return { items, categories };
};
