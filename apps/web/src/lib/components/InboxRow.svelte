<script lang="ts">
	import type { Category, InboxItem } from '#lib/types.ts';
	import { formatMoney } from '#lib/utils/money.ts';
	import { signedAmount } from '#lib/utils/labels.ts';
	import {
		ignoreReasonLabel,
		isTransfer,
		transferArrow,
		txState
	} from '#lib/utils/transactions.ts';
	import Icon from './Icon.svelte';
	import SuggestionChip from './SuggestionChip.svelte';

	let {
		item,
		categories,
		busy = false,
		onConfirm,
		onIgnore,
		onRestore,
		onPick,
		onOpen
	}: {
		item: InboxItem;
		categories: Category[];
		busy?: boolean;
		/** confirm with the current suggestion */
		onConfirm: (item: InboxItem) => void;
		onIgnore: (item: InboxItem) => void;
		onRestore: (item: InboxItem) => void;
		/** open the transaction sheet (optionally at one action) to change the suggestion */
		onPick: (item: InboxItem, section?: 'match' | 'expense' | 'transfer' | 'bill' | null) => void;
		/** v5: open the transaction sheet (tapping the row) */
		onOpen?: (item: InboxItem) => void;
	} = $props();

	const title = $derived(item.payee || item.description);
	const sub = $derived(
		item.payee && item.description !== item.payee ? item.description : (item.memo ?? '')
	);
	const transfer = $derived(item.suggestion?.kind === 'transfer');
	const canConfirm = $derived(!!item.suggestion && !transfer);
	const peerArrow = $derived(isTransfer(item) ? transferArrow(item) : '');
	const outcome = $derived.by(() => {
		const c = item.confirmed;
		if (item.status === 'ignored') return ignoreReasonLabel(item);
		if (!c) return txState(item).label;
		if (c.auto && c.itemId) {
			const name = item.match?.itemName ?? c.itemName;
			return `Auto-matched${name ? ` · ${name}` : ''}`;
		}
		return txState(item).label;
	});
</script>

<li
	class="flex flex-col gap-2 py-3 {item.status !== 'pending' ? 'text-base-content/70' : ''}"
	data-testid="inbox-row"
	data-status={item.status}
>
	<div class="relative flex items-start gap-3">
		{#if onOpen}
			<button
				type="button"
				class="absolute inset-0 z-0 rounded-lg"
				aria-label="Open {title}"
				data-testid="inbox-open"
				onclick={() => onOpen(item)}
			></button>
		{/if}
		<div class="pointer-events-none relative min-w-0 flex-1">
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
			<p class="truncate text-xs text-base-content/50" data-testid="inbox-account">
				{item.accountName ?? 'No account'}{#if peerArrow}<span class="font-medium text-info">
						{peerArrow}</span
					>{/if}
			</p>
		</div>
		<p
			class="pointer-events-none relative shrink-0 font-semibold tabular {item.amountCents < 0
				? 'text-error'
				: 'text-success'}"
			data-testid="inbox-amount"
		>
			{signedAmount(item.amountCents, formatMoney)}
		</p>
	</div>

	<div class="flex flex-wrap items-center justify-between gap-2">
		{#if item.status === 'pending'}
			<SuggestionChip
				suggestion={item.suggestion}
				{categories}
				transferTo={item.transfer?.peerAccountName ?? null}
				onclick={() => onPick(item, transfer ? 'transfer' : undefined)}
			/>
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
						class="btn min-h-11 rounded-full border border-base-300 btn-ghost px-4 btn-sm"
						disabled={busy}
						aria-label="Ignore {title}"
						onclick={() => onIgnore(item)}
					>
						<Icon name="circle-minus" size={16} /> Ignore
					</button>
					{#if transfer}
						<button
							type="button"
							class="btn min-h-11 rounded-full px-4 btn-primary btn-sm"
							disabled={busy}
							aria-label="Mark {title} as a transfer"
							onclick={() => onPick(item, 'transfer')}
						>
							<Icon name="arrow-left-right" size={16} /> Transfer
						</button>
					{:else}
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
					<Icon
						name={isTransfer(item) ? 'arrow-left-right' : 'circle-minus'}
						size={14}
						class="text-base-content/50"
					/>
					{outcome}
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
