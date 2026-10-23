<script lang="ts">
	import type { Snippet } from 'svelte';
	import Icon from './Icon.svelte';

	let {
		open = $bindable(false),
		title,
		children
	}: {
		open?: boolean;
		title: string;
		children: Snippet;
	} = $props();

	let dialog = $state<HTMLDialogElement | null>(null);

	$effect(() => {
		if (!dialog) return;
		if (open && !dialog.open) dialog.showModal();
		else if (!open && dialog.open) dialog.close();
	});
</script>

<!-- daisyUI bottom-sheet modal built on <dialog> so focus trapping and Escape come for free -->
<dialog
	bind:this={dialog}
	class="modal modal-bottom sm:modal-middle"
	aria-labelledby="sheet-title"
	onclose={() => (open = false)}
>
	<div
		class="modal-box mx-auto w-full max-w-xl rounded-t-3xl bg-base-100 p-0 safe-bottom sm:rounded-3xl"
	>
		<div class="mx-auto mt-2 h-1 w-10 rounded-full bg-base-300 sm:hidden"></div>
		<div class="flex items-center justify-between px-5 pt-3 pb-1">
			<h2 id="sheet-title" class="font-semibold">{title}</h2>
			<form method="dialog">
				<button class="btn btn-circle min-h-11 min-w-11 btn-ghost btn-sm" aria-label="Close">
					<Icon name="x" size={20} />
				</button>
			</form>
		</div>
		<div class="max-h-[70dvh] overflow-y-auto px-5 pb-5">
			{#if open}{@render children()}{/if}
		</div>
	</div>
	<form method="dialog" class="modal-backdrop">
		<button aria-label="Close">close</button>
	</form>
</dialog>
