import { redirect } from '@sveltejs/kit';
import type { RequestHandler } from './$types';

/** v6: the calendar is a view inside Bills; old links (and push deep links) keep their month. */
export const GET: RequestHandler = ({ url }) => {
	const m = url.searchParams.get('m');
	const d = url.searchParams.get('d');
	const q = new URLSearchParams({ view: 'calendar' });
	if (m && /^\d{4}-(0[1-9]|1[0-2])$/.test(m)) q.set('m', m);
	if (d && /^\d{4}-\d{2}-\d{2}$/.test(d)) q.set('d', d);
	redirect(308, `/bills?${q}`);
};
