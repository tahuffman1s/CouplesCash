import { redirect } from '@sveltejs/kit';
import type { RequestHandler } from './$types';

/** v6: goals are a section of Money (detail pages stay). */
export const GET: RequestHandler = () => redirect(308, '/accounts#goals');
