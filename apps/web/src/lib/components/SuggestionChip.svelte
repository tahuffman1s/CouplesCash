<script lang="ts">
	import type { Category, Suggestion } from '#lib/types.ts';
	import { KIND_ICON } from '#lib/utils/labels.ts';
	import { formatDate } from '#lib/utils/dates.ts';
	import Icon from './Icon.svelte';

	let {
		suggestion,
		categories,
		onclick,
		label = 'Change suggestion'
	}: {
		suggestion: Suggestion;
		categories: Category[];
		/** when given, the chip is a button that opens the picker */
		onclick?: () => void;
		label?: string;
	} = $props();

	const colour = $derived(
		suggestion?.kind === 'category'
			? (categories.find((c) => c.id === suggestion.categoryId)?.color ?? 'var(--color-neutral)')
			: null
	);
	const base =
		'badge h-auto min-h-7 gap-1.5 rounded-full border px-2.5 py-1 text-xs font-medium whitespace-nowrap';
</script>

{#snippet body()}
	{#if !suggestion}
		<Icon name="tag" size={12} class="text-base-content/50" /> No suggestion
	{:else if suggestion.kind === 'category'}
		<span class="h-2 w-2 shrink-0 rounded-full" style:background={colour} aria-hidden="true"></span>
		{suggestion.categoryName}
	{:else if suggestion.kind === 'occurrence'}
		<Icon name={KIND_ICON[suggestion.itemKind]} size={12} />
		{suggestion.itemName} · due {formatDate(suggestion.dueDate)}
	{:else}
		<Icon name="arrow-left-right" size={12} /> Transfer
	{/if}
{/snippet}

{#if onclick}
	<button
		type="button"
		class="{base} {suggestion
			? 'border-base-300 bg-base-200/70'
			: 'border-dashed border-base-300 bg-transparent text-base-content/70'} min-h-9"
		aria-label="{label}: {!suggestion
			? 'no suggestion'
			: suggestion.kind === 'category'
				? suggestion.categoryName
				: suggestion.kind === 'occurrence'
					? suggestion.itemName
					: 'transfer'}"
		data-testid="suggestion-chip"
		{onclick}
	>
		{@render body()}
		<Icon name="chevron-down" size={12} class="text-base-content/50" />
	</button>
{:else}
	<span
		class="{base} {suggestion
			? 'border-base-300 bg-base-200/70'
			: 'border-dashed border-base-300 text-base-content/70'}"
		data-testid="suggestion-chip"
	>
		{@render body()}
	</span>
{/if}
