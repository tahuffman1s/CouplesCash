<script lang="ts">
	import type { AccountTransaction, InboxItem } from '#lib/types.ts';
	import { formatMoney } from '#lib/utils/money.ts';
	import { signedAmount } from '#lib/utils/labels.ts';
	import { statusChip } from '#lib/utils/transactions.ts';

	let {
		tx,
		onopen
	}: {
		tx: AccountTransaction | InboxItem;
		onopen: (tx: AccountTransaction | InboxItem) => void;
	} = $props();

	const title = $derived(tx.payee || tx.description);
	const sub = $derived(
		[tx.payee && tx.description !== tx.payee ? tx.description : null, tx.memo]
			.filter(Boolean)
			.join(' · ')
	);
	const chip = $derived(statusChip(tx));
	const CHIP_CLASS: Record<string, string> = {
		pending: 'bg-warning/20 text-warning-content',
		review: 'bg-warning/15 text-warning-content',
		expense: 'bg-base-200 text-base-content/80',
		manual: 'bg-base-200 text-base-content/80',
		paid: 'bg-success/15 text-success',
		transfer: 'bg-info/15 text-info',
		ignored: 'bg-base-200 text-base-content/60',
		dropped: 'bg-base-200 text-base-content/60',
		superseded: 'bg-base-200 text-base-content/60'
	};
</script>

<li data-testid="tx-row" data-id={tx.id} data-status={tx.status}>
	<button
		type="button"
		class="flex min-h-14 w-full items-center gap-3 py-2.5 text-left"
		aria-label="Open {title} {signedAmount(tx.amountCents, formatMoney)}"
		onclick={() => onopen(tx)}
	>
		<span class="min-w-0 flex-1">
			<span
				class="block truncate font-medium {tx.status === 'ignored' && chip.kind !== 'transfer'
					? 'text-base-content/60 line-through'
					: ''}">{title}</span
			>
			{#if sub}<span class="block truncate text-xs text-base-content/55">{sub}</span>{/if}
			<span class="mt-1 flex flex-wrap items-center gap-1">
				<span
					class="rounded-full px-2 py-0.5 text-[0.7rem] leading-4 font-medium {CHIP_CLASS[
						chip.kind
					] ?? 'bg-base-200'}"
					data-testid="tx-status">{chip.label}</span
				>
				{#if tx.pending && chip.kind !== 'pending'}
					<span
						class="rounded-full bg-warning/20 px-2 py-0.5 text-[0.7rem] leading-4 font-medium text-warning-content"
						data-testid="tx-pending">Pending</span
					>
				{/if}
			</span>
		</span>
		<span
			class="shrink-0 font-semibold tabular {tx.amountCents < 0 ? '' : 'text-success'}"
			data-testid="tx-amount"
		>
			{signedAmount(tx.amountCents, formatMoney)}
		</span>
	</button>
</li>
