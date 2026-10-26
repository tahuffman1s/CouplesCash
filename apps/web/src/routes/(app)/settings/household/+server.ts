import { redirect } from '@sveltejs/kit';
import type { RequestHandler } from './$types';

/** v6: one settings page. */
export const GET: RequestHandler = () => redirect(308, '/settings#household');
