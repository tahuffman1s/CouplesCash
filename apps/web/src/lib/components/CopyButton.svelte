<script lang="ts">
	import Icon from './Icon.svelte';
	import { copyText } from '#lib/utils/plan.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	/**
	 * v7 Copy: puts the number on the clipboard the way a bank app wants it ("253.17"), for the
	 * "Left on Thursday" hero and the "Move to savings" step.
	 */
	let {
		cents,
		label = 'Copy',
		size = 'sm',
		testid = 'copy',
		tone = 'ghost'
	}: {
		cents: number;
		label?: string;
		size?: 'sm' | 'md';
		testid?: string;
		tone?: 'ghost' | 'onPrimary';
	} = $props();

	let copied = $state(false);
	let timer: ReturnType<typeof setTimeout> | undefined;

	async function copy() {
		const text = copyText(cents);
		try {
			await navigator.clipboard.writeText(text);
			copied = true;
			clearTimeout(timer);
			timer = setTimeout(() => (copied = false), 1600);
			toasts.show(`Copied ${text}`, 'success', 2500);
		} catch {
			toasts.show(`Couldn't copy here. The number is ${text}`, 'info', 6000);
		}
	}
</script>

<button
	type="button"
	class="btn min-h-11 shrink-0 gap-1.5 rounded-full px-4 {size === 'sm' ? 'btn-sm' : ''} {tone ===
	'onPrimary'
		? 'border-0 bg-primary-content/20 text-primary-content hover:bg-primary-content/30'
		: 'border border-base-300 btn-ghost'}"
	aria-label="{label} {copyText(cents)}"
	data-testid={testid}
	onclick={copy}
>
	<Icon name={copied ? 'check' : 'copy'} size={16} />
	{copied ? 'Copied' : label}
</button>
