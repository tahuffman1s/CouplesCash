<script lang="ts">
	import type { Category } from '#lib/types.ts';
	import Icon from './Icon.svelte';

	let {
		categories,
		onpick,
		disabled = false,
		selectedId = undefined
	}: {
		categories: Category[];
		onpick: (categoryId: string | null, name: string) => void;
		disabled?: boolean;
		/** highlights the current category (undefined = none highlighted) */
		selectedId?: string | null;
	} = $props();
</script>

<div class="grid grid-cols-3 gap-2" role="group" aria-label="Categories">
	{#each categories as c (c.id)}
		<button
			type="button"
			class="flex min-h-16 flex-col items-center justify-center gap-1 rounded-2xl border p-2 text-xs font-medium hover:bg-base-200 {selectedId ===
			c.id
				? 'border-primary bg-primary/5'
				: 'border-base-300/60 bg-base-100'}"
			aria-pressed={selectedId === undefined ? undefined : selectedId === c.id}
			{disabled}
			onclick={() => onpick(c.id, c.name)}
		>
			<span
				class="flex h-8 w-8 items-center justify-center rounded-xl text-white"
				style:background={c.color}
			>
				<Icon name={c.icon} size={16} />
			</span>
			<span class="max-w-full truncate">{c.name}</span>
		</button>
	{/each}
	<button
		type="button"
		class="flex min-h-16 flex-col items-center justify-center gap-1 rounded-2xl border border-dashed border-base-300 p-2 text-xs font-medium text-base-content/70 hover:bg-base-200"
		{disabled}
		onclick={() => onpick(null, 'no category')}
	>
		<span class="flex h-8 w-8 items-center justify-center rounded-xl bg-base-200">
			<Icon name="tag" size={16} />
		</span>
		No category
	</button>
</div>
