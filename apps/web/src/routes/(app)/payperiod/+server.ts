import { redirect } from '@sveltejs/kit';
import type { RequestHandler } from './$types';

/** v6: the pay period lives in the Week header ("Next payday Fri Nov 6 · $3,200"). */
export const GET: RequestHandler = () => redirect(308, '/week');
