import { redirect } from '@sveltejs/kit';
import type { RequestHandler } from './$types';

/** v6: pending by account is a section of Money. */
export const GET: RequestHandler = () => redirect(308, '/accounts#pending');
