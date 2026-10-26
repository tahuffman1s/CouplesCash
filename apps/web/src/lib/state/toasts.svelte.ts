import { untrack } from 'svelte';

export type ToastAction = { label: string; run: () => void };
export type Toast = {
	id: number;
	text: string;
	tone: 'info' | 'success' | 'error';
	action?: ToastAction;
};

let seq = 0;
const list = $state<Toast[]>([]);

/**
 * Tiny toast queue. Mutations run inside `untrack` so `show()` can be called from an `$effect`
 * (e.g. "saved" feedback after a form action) without the effect subscribing to the list and
 * re-running itself. A toast may carry one action ("Undo"); toasts with an action stay longer.
 */
export const toasts = {
	get list() {
		return list;
	},
	show(text: string, tone: Toast['tone'] = 'info', ms = 3200, action?: ToastAction) {
		const id = ++seq;
		untrack(() => list.push({ id, text, tone, action }));
		setTimeout(() => toasts.dismiss(id), action ? Math.max(ms, 6000) : ms);
		return id;
	},
	dismiss(id: number) {
		untrack(() => {
			const i = list.findIndex((t) => t.id === id);
			if (i >= 0) list.splice(i, 1);
		});
	}
};
