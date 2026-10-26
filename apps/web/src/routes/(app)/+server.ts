import { redirect } from '@sveltejs/kit';
import type { RequestHandler } from './$types';

/** v6: Week is home (PLAN-V6 §1). */
export const GET: RequestHandler = () => redirect(307, '/week');
