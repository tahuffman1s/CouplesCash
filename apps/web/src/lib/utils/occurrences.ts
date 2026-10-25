import type { Occurrence } from '#lib/types.ts';
import { apiFetch } from './client';
import { today } from './dates.ts';

/** Flips the paid state of an occurrence through the BFF proxy and returns the server's version. */
export function togglePaid(occ: Occurrence): Promise<Occurrence> {
	const path = `/recurring/${occ.itemId}/occurrences/${occ.dueDate}/paid`;
	return occ.paid
		? apiFetch<Occurrence>(path, { method: 'DELETE' })
		: apiFetch<Occurrence>(path, { method: 'PUT', body: JSON.stringify({}) });
}

/** Optimistic local version of the toggled occurrence. */
export function optimisticToggle(occ: Occurrence, userId: string): Occurrence {
	if (occ.paid) {
		return {
			...occ,
			paid: null,
			varianceCents: null,
			status: occ.kind !== 'income' && occ.dueDate < today() ? 'overdue' : 'planned'
		};
	}
	return {
		...occ,
		paid: {
			paidAt: new Date().toISOString(),
			paidBy: userId,
			amountCents: occ.amountCents,
			auto: false
		},
		varianceCents: 0,
		status: 'cleared'
	};
}

export const occurrenceKey = (o: Occurrence) => `${o.itemId}:${o.dueDate}`;
