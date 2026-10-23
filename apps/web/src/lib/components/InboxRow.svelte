<script lang="ts">
	import type { Category, InboxItem } from '#lib/types.ts';
	import { formatMoney } from '#lib/utils/money.ts';
	import { formatDate } from '#lib/utils/dates.ts';
	import { signedAmount } from '#lib/utils/labels.ts';
	import Icon from './Icon.svelte';
	import SuggestionChip from './SuggestionChip.svelte';

	let {
		item,
		categories,
		busy = false,
		onConfirm,
		onIgnore,
		onRestore,
		onPick
	}: {
		item: InboxItem;
		categories: Category[];
		busy?: boolean;
		/** confirm with the current suggestion */
		onConfirm: (item: InboxItem) => void;
		onIgnore: (item: InboxItem) => void;
		onRestore: (item: InboxItem) => void;
		/** open the picker to change the suggestion */
		onPick: (item: InboxItem) => void;
	} = $props();

	const title = $derived(item.payee || item.description);
	const sub = $derived(
		item.payee && item.description !== item.payee ? item.description : (item.memo ?? '')
	);
	const transfer = $derived(item.suggestion?.kind === 'transfer');
	const canConfirm = $derived(!!item.suggestion && !transfer);
	const outcome = $derived.by(() => {
		const c = item.confirmed;
		if (!c) return '';
		if (c.auto) return `Auto-matched${c.itemName ? ` · ${c.itemName}` : ''}`;
		if (c.itemId) {
			const name =
				c.itemName ??
				(item.suggestion?.kind === 'occurrence' && item.suggestion.itemId === c.itemId
					? item.suggestion.itemName
					: 'bill');
			return `Paid ${name}${c.dueDate ? ` · ${formatDate(c.dueDate)}` : ''}`;
		}
		const cat =
			c.categoryName ??
			(item.suggestion?.kind === 'category' ? item.suggestion.categoryName : null);
		return cat ? `Expense in ${cat}` : 'Expense';
	});
</script>

<li
	class="flex flex-col gap-2 py-3 {item.status !== 'pending' ? 'text-base-content/70' : ''}"
	data-testid="inbox-row"
	data-status={item.status}
>
	<div class="flex items-start gap-3">
		<div class="min-w-0 flex-1">
			<p class="flex items-center gap-1.5">
				<span class="truncate font-medium {item.status === 'ignored' ? 'line-through' : ''}"
					>{title}</span
				>
				{#if item.pending}
					<span
						class="badge shrink-0 border-0 bg-warning/20 px-1.5 badge-xs text-[0.65rem] font-medium text-warning-content"
						>Pending</span
					>
				{/if}
				{#if item.source === 'csv'}
					<span
						class="badge shrink-0 gap-0.5 border-0 bg-base-200 px-1.5 badge-xs text-[0.65rem] font-medium text-base-content/70"
						><Icon name="file-spreadsheet" size={10} /> CSV</span
					>
				{/if}
			</p>
			{#if sub}
				<p class="truncate text-xs text-base-content/60">{sub}</p>
			{/if}
			<p class="truncate text-xs text-base-content/50">
				{item.accountName ?? 'No account'}
			</p>
		</div>
		<p
			class="shrink-0 font-semibold tabular {item.amountCents < 0 ? 'text-error' : 'text-success'}"
			data-testid="inbox-amount"
		>
			{signedAmount(item.amountCents, formatMoney)}
		</p>
	</div>

	<div class="flex flex-wrap items-center justify-between gap-2">
		{#if item.status === 'pending'}
			<SuggestionChip suggestion={item.suggestion} {categories} onclick={() => onPick(item)} />
			<div class="flex items-center gap-1">
				{#if canConfirm}
					<button
						type="button"
						class="btn min-h-11 rounded-full px-4 btn-primary btn-sm"
						disabled={busy}
						aria-label="Confirm {title}"
						onclick={() => onConfirm(item)}
					>
						<Icon name="check" size={16} strokeWidth={2.5} /> Confirm
					</button>
					<button
						type="button"
						class="btn min-h-11 rounded-full btn-ghost px-3 btn-sm"
						disabled={busy}
						aria-label="Ignore {title}"
						onclick={() => onIgnore(item)}
					>
						Ignore
					</button>
				{:else}
					<button
						type="button"
						class="btn min-h-11 rounded-full px-4 btn-sm {transfer
							? 'btn-primary'
							: 'border border-base-300 btn-ghost'}"
						disabled={busy}
						aria-label="Ignore {title}"
						onclick={() => onIgnore(item)}
					>
						<Icon name="circle-minus" size={16} /> Ignore
					</button>
					{#if !transfer}
						<button
							type="button"
							class="btn min-h-11 rounded-full px-4 btn-primary btn-sm"
							disabled={busy}
							aria-label="Confirm {title}"
							onclick={() => onPick(item)}
						>
							<Icon name="check" size={16} strokeWidth={2.5} /> Confirm
						</button>
					{/if}
				{/if}
			</div>
		{:else}
			<p class="flex items-center gap-1.5 text-xs font-medium" data-testid="inbox-outcome">
				{#if item.status === 'confirmed'}
					<Icon name="circle-check" size={14} class="text-success" />
					{outcome}
					{#if item.confirmed?.byName}<span class="font-normal text-base-content/50"
							>· {item.confirmed.byName}</span
						>{/if}
				{:else}
					<Icon name="circle-minus" size={14} class="text-base-content/50" /> Ignored
				{/if}
			</p>
			<button
				type="button"
				class="btn min-h-11 rounded-full border border-base-300 btn-ghost px-3 btn-sm"
				disabled={busy}
				aria-label="Restore {title}"
				onclick={() => onRestore(item)}
			>
				<Icon name="undo-2" size={16} /> Restore
			</button>
		{/if}
	</div>
</li>
