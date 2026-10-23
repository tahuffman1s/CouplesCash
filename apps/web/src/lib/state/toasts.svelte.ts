import { untrack } from 'svelte';

export type Toast = { id: number; text: string; tone: 'info' | 'success' | 'error' };

let seq = 0;
const list = $state<Toast[]>([]);

/**
 * Tiny toast queue. Mutations run inside `untrack` so `show()` can be called from an `$effect`
 * (e.g. "saved" feedback after a form action) without the effect subscribing to the list and
 * re-running itself.
 */
export const toasts = {
	get list() {
		return list;
	},
	show(text: string, tone: Toast['tone'] = 'info', ms = 3200) {
		const id = ++seq;
		untrack(() => list.push({ id, text, tone }));
		setTimeout(() => toasts.dismiss(id), ms);
		return id;
	},
	dismiss(id: number) {
		untrack(() => {
			const i = list.findIndex((t) => t.id === id);
			if (i >= 0) list.splice(i, 1);
		});
	}
};
