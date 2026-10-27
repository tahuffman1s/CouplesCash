<script lang="ts">
	import { enhance } from '$app/forms';
	import type { ChecklistItem } from '#lib/types.ts';
	import Icon from './Icon.svelte';
	import { formatMoney } from '#lib/utils/money.ts';
	import { checklistProgress, checklistText } from '#lib/utils/plan.ts';

	/**
	 * v7 payday checklist: paycheck and fun money (the move to savings is the plan's bottom line on
	 * the last day of the week). Items tick themselves
	 * from the bank feed ("auto"); "Done" ticks one by hand when the bank is slow. All done → one
	 * line, "Payday done ✓".
	 */
	let { items, isPayday }: { items: ChecklistItem[]; isPayday: boolean } = $props();

	const progress = $derived(checklistProgress(items));
	let busy = $state<string | null>(null);
	const keyOf = (i: ChecklistItem) => `${i.key}:${i.itemId ?? ''}:${i.dueDate ?? ''}`;
	const submit = (k: string) => () => {
		busy = k;
		return async ({ update }: { update: (o?: { reset?: boolean }) => Promise<void> }) => {
			await update({ reset: false });
			busy = null;
		};
	};
</script>

{#snippet row(item: ChecklistItem)}
	{@const k = keyOf(item)}
	{@const shown = checklistText(item)}
	<li
		class="flex items-center gap-x-2 gap-y-1 py-1.5"
		data-testid="checklist-item"
		data-key={item.key}
		data-done={item.done ? 'true' : 'false'}
	>
		<Icon
			name={item.done ? 'circle-check' : 'circle'}
			size={22}
			class="shrink-0 {item.done ? 'text-success' : 'text-base-content/35'}"
		/>
		<span class="min-w-0 flex-1 text-sm {item.done ? 'text-base-content/60' : ''}">
			{shown.text}
			{#if item.done && item.auto}
				<span
					class="ml-1 badge border-0 bg-info/15 px-1.5 badge-xs text-[0.65rem] text-info"
					data-testid="checklist-auto">auto</span
				>
			{/if}
		</span>
		{#if shown.showAmount}
			<span class="shrink-0 font-mono text-sm tabular"
				>{item.key === 'paycheck' ? '+' : ''}{formatMoney(item.amountCents)}</span
			>
		{/if}
		<div class="flex shrink-0 justify-end gap-1.5">
			{#if !item.done}
				<form method="POST" action="?/paid" use:enhance={submit(k)}>
					<input type="hidden" name="itemId" value={item.itemId ?? ''} />
					<input type="hidden" name="dueDate" value={item.dueDate ?? ''} />
					<input type="hidden" name="paid" value="1" />
					<button
						class="btn min-h-11 rounded-full border border-primary/40 btn-ghost px-4 text-primary btn-sm"
						disabled={busy === k}
						data-testid="checklist-done"
					>
						{#if busy === k}<span class="loading loading-xs loading-spinner"></span>{/if}
						Done
					</button>
				</form>
			{:else if !item.auto}
				<form method="POST" action="?/paid" use:enhance={submit(k)}>
					<input type="hidden" name="itemId" value={item.itemId ?? ''} />
					<input type="hidden" name="dueDate" value={item.dueDate ?? ''} />
					<input type="hidden" name="paid" value="0" />
					<button
						class="btn min-h-11 rounded-full btn-ghost px-3 text-xs btn-sm"
						disabled={busy === k}
						data-testid="checklist-undo">Undo</button
					>
				</form>
			{/if}
		</div>
	</li>
{/snippet}

{#if progress.allDone}
	<details class="rounded-2xl bg-success/10 px-4" data-testid="payday-done">
		<summary
			class="flex min-h-12 cursor-pointer list-none items-center gap-2 text-sm font-semibold [&::-webkit-details-marker]:hidden"
		>
			<Icon name="circle-check" size={18} class="text-success" />
			<span class="flex-1">Payday done ✓</span>
			<Icon name="chevron-down" size={16} class="text-base-content/40" />
		</summary>
		<ul class="divide-y divide-base-300/60 pb-2">
			{#each items as item (keyOf(item))}{@render row(item)}{/each}
		</ul>
	</details>
{:else}
	<section
		class="card border border-primary/30 bg-primary/5 shadow-sm"
		aria-labelledby="payday-h"
		data-testid="payday-checklist"
	>
		<div class="card-body gap-1 p-4">
			<div class="flex items-baseline justify-between gap-2">
				<h2 id="payday-h" class="flex items-center gap-2 font-semibold">
					<Icon name="calendar-check" size={18} class="text-primary" />
					{isPayday ? "It's payday" : 'Payday to-do'}
				</h2>
				<span class="text-xs text-base-content/60 tabular" data-testid="checklist-progress"
					>{progress.done} of {progress.total} done</span
				>
			</div>
			<ul class="divide-y divide-base-300/60">
				{#each items as item (keyOf(item))}{@render row(item)}{/each}
			</ul>
		</div>
	</section>
{/if}
