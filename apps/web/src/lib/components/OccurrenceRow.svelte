<script lang="ts">
	import type { Occurrence } from '#lib/types.ts';
	import { formatMoney } from '#lib/utils/money.ts';
	import { relativeDay, formatDate, today } from '#lib/utils/dates.ts';
	import Icon from './Icon.svelte';

	let {
		occ,
		onToggle,
		showDate = true,
		busy = false
	}: {
		occ: Occurrence;
		onToggle?: (occ: Occurrence) => void;
		showDate?: boolean;
		busy?: boolean;
	} = $props();

	const overdue = $derived(!occ.paid && occ.kind !== 'income' && occ.dueDate < today());
	const colour = $derived(occ.categoryColor ?? 'var(--color-base-300)');
	const autoPaid = $derived(!!occ.paid?.auto);
</script>

<li class="flex items-center gap-3 py-2.5">
	<span class="h-10 w-1.5 shrink-0 rounded-full" style:background={colour} aria-hidden="true"
	></span>
	<a href="/bills/{occ.itemId}" class="min-w-0 flex-1 rounded-lg">
		<p class="flex items-center gap-1.5">
			<span class="truncate font-medium {occ.paid ? 'text-base-content/50 line-through' : ''}">
				{occ.name}
			</span>
			{#if occ.autoPay && !autoPaid}
				<span
					class="badge shrink-0 gap-0.5 border-0 bg-secondary/15 px-1.5 badge-xs text-[0.65rem] font-medium text-secondary"
				>
					<Icon name="zap" size={10} strokeWidth={2.5} /> Autopay
				</span>
			{/if}
		</p>
		<p class="truncate text-xs text-base-content/60">
			{#if autoPaid}
				<span class="inline-flex items-center gap-1 font-medium text-secondary">
					<Icon name="bot" size={12} /> Auto-paid
				</span>
				{#if showDate}<span aria-hidden="true"> · </span><span>{formatDate(occ.dueDate)}</span>{/if}
			{:else if showDate}
				<span class={overdue ? 'font-medium text-error' : ''}>{relativeDay(occ.dueDate)}</span>
				<span aria-hidden="true"> · </span>
				<span>{formatDate(occ.dueDate)}</span>
			{:else if occ.categoryName}
				{occ.categoryName}
			{/if}
		</p>
	</a>
	<span
		class="shrink-0 text-sm font-semibold tabular {occ.kind === 'income'
			? 'text-success'
			: occ.paid
				? 'text-base-content/50'
				: ''}"
	>
		{occ.kind === 'income' ? '+' : ''}{formatMoney(occ.paid?.amountCents ?? occ.amountCents)}
	</span>
	{#if onToggle}
		{#if autoPaid}
			<button
				type="button"
				class="btn min-h-11 rounded-full border border-base-300 btn-ghost px-3 btn-sm"
				aria-label="Undo auto-pay for {occ.name}"
				disabled={busy}
				onclick={() => onToggle(occ)}
			>
				<Icon name="undo-2" size={16} /> Undo
			</button>
		{:else}
			<button
				type="button"
				class="btn btn-circle min-h-11 min-w-11 border btn-sm {occ.paid
					? 'text-success-content btn-success'
					: 'border-base-300 btn-ghost'}"
				aria-pressed={!!occ.paid}
				aria-label={occ.paid ? `Mark ${occ.name} unpaid` : `Mark ${occ.name} paid`}
				disabled={busy}
				onclick={() => onToggle(occ)}
			>
				<Icon name="check" size={18} strokeWidth={2.5} />
			</button>
		{/if}
	{/if}
</li>
