<script lang="ts">
	import type { AccountTransaction, InboxItem } from '#lib/types.ts';
	import { signedMoney } from '#lib/utils/money.ts';
	import { autoReason, isAuto, statusChip } from '#lib/utils/transactions.ts';
	import Amount from './Amount.svelte';

	let {
		tx,
		onopen,
		showAccount = false
	}: {
		tx: AccountTransaction | InboxItem;
		onopen: (tx: AccountTransaction | InboxItem) => void;
		/** v6: the all-accounts Transactions tab names the account on every row */
		showAccount?: boolean;
	} = $props();

	const title = $derived(tx.payee || tx.description);
	const sub = $derived(
		[
			showAccount ? (tx.accountName ?? 'No account') : null,
			tx.payee && tx.description !== tx.payee ? tx.description : null,
			tx.memo
		]
			.filter(Boolean)
			.join(' · ')
	);
	const chip = $derived(statusChip(tx));
	const auto = $derived(isAuto(tx));
	const CHIP_CLASS: Record<string, string> = {
		pending: 'bg-warning/20 text-pending',
		review: 'bg-warning/15 text-pending',
		expense: 'bg-base-200 text-base-content/80',
		manual: 'bg-base-200 text-base-content/80',
		added: 'bg-primary/10 text-primary',
		paid: 'bg-success/15 text-success',
		transfer: 'bg-info/15 text-info',
		ignored: 'bg-base-200 text-base-content/60',
		dropped: 'bg-base-200 text-base-content/60',
		superseded: 'bg-base-200 text-base-content/60'
	};
	const muted = $derived(tx.status === 'ignored');
</script>

<li data-testid="tx-row" data-id={tx.id} data-status={tx.status}>
	<button
		type="button"
		class="flex min-h-14 w-full items-center gap-3 py-2.5 text-left"
		aria-label="Open {title} {signedMoney(tx.amountCents)}"
		onclick={() => onopen(tx)}
	>
		<span class="min-w-0 flex-1">
			<span
				class="block truncate font-medium {tx.status === 'ignored' && chip.kind !== 'transfer'
					? 'text-base-content/60 line-through'
					: ''}">{title}</span
			>
			{#if sub}<span class="block truncate text-xs text-base-content/55" data-testid="tx-sub"
					>{sub}</span
				>{/if}
			<span class="mt-1 flex flex-wrap items-center gap-1">
				<span
					class="rounded-full px-2 py-0.5 text-[0.7rem] leading-4 font-medium {CHIP_CLASS[
						chip.kind
					] ?? 'bg-base-200'}"
					data-testid="tx-status">{chip.label}</span
				>
				{#if tx.pending && chip.kind !== 'pending'}
					<span
						class="rounded-full bg-warning/20 px-2 py-0.5 text-[0.7rem] leading-4 font-medium text-pending"
						data-testid="tx-pending">Pending</span
					>
				{/if}
				{#if auto}
					<span
						class="rounded-full bg-secondary/15 px-2 py-0.5 text-[0.7rem] leading-4 font-medium text-secondary"
						data-testid="tx-auto"
						title={autoReason(tx)}>auto</span
					>
				{/if}
			</span>
			{#if auto && tx.matchDetail?.reason}
				<span
					class="mt-0.5 block truncate text-[0.7rem] text-base-content/55"
					data-testid="tx-reason">{autoReason(tx)}</span
				>
			{/if}
		</span>
		<Amount cents={tx.amountCents} {muted} class="shrink-0 font-semibold" testid="tx-amount" />
	</button>
</li>
