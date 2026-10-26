import { redirect } from '@sveltejs/kit';
import type { RequestHandler } from './$types';

/** v6: the inbox is the To-review section of Transactions. */
export const GET: RequestHandler = () => redirect(308, '/transactions');
