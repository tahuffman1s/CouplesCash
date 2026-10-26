<script lang="ts">
	import { onMount } from 'svelte';
	import Icon from './Icon.svelte';

	/**
	 * v6: the one-line helper under a main screen's title on first visit. Dismissing it is remembered
	 * per device (localStorage; private mode just shows it again next time).
	 */
	let { id, text }: { id: string; text: string } = $props();

	const key = $derived(`cc-help-dismissed:${id}`);
	let show = $state(false);
	onMount(() => {
		try {
			show = localStorage.getItem(key) !== '1';
		} catch {
			show = true;
		}
	});
	function dismiss() {
		show = false;
		try {
			localStorage.setItem(key, '1');
		} catch {
			// storage blocked: hidden until the next visit
		}
	}
</script>

{#if show}
	<p
		class="flex items-start gap-2 rounded-2xl bg-base-200/70 py-1.5 pr-1 pl-3 text-sm text-base-content/75"
		data-testid="helper-text"
	>
		<Icon name="lightbulb" size={16} class="mt-1 shrink-0 text-accent" />
		<span class="flex-1 py-1">{text}</span>
		<button
			type="button"
			class="btn btn-circle min-h-11 min-w-11 shrink-0 btn-ghost btn-sm"
			aria-label="Got it, hide this tip"
			onclick={dismiss}
		>
			<Icon name="x" size={14} />
		</button>
	</p>
{/if}
