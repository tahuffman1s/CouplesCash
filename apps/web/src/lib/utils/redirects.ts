/**
 * v7: old URLs land somewhere sensible (pure, unit-tested; applied in `hooks.server.ts`).
 * Everything that was removed goes to the Friday plan, the calendar to Bills, the inbox to
 * Transactions, the old settings pages to their section of the one settings page.
 * `/accounts/[id]`, `/accounts/new`, `/imports/csv` and `/settings/banks|passkeys` stay.
 */
const TO_WEEK = [
	'/insights',
	'/goals',
	'/todos',
	'/activity',
	'/expenses',
	'/more',
	'/payperiod',
	'/pending',
	'/transfers'
];

export function legacyRedirect(pathname: string): string | null {
	const p = pathname.length > 1 ? pathname.replace(/\/+$/, '') : pathname;
	if (p === '/accounts') return '/week';
	if (TO_WEEK.some((base) => p === base || p.startsWith(`${base}/`))) return '/week';
	if (p === '/calendar') return '/bills';
	if (p === '/inbox') return '/transactions';
	if (p === '/settings/household') return '/settings#household';
	if (p === '/settings/notifications') return '/settings#you';
	return null;
}
