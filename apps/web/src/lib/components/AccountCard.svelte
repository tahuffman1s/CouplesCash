<script lang="ts">
	import type { Account } from '#lib/types.ts';
	import { LIABILITY_TYPES } from '#lib/types.ts';
	import { formatMoney, signedMoney } from '#lib/utils/money.ts';
	import Icon from './Icon.svelte';

	let {
		account,
		pendingCount = 0,
		pendingOutCents = account.pendingOutCents ?? 0
	}: {
		account: Account;
		/** v6: from `GET /pending`; the line links to the account's Pending filter */
		pendingCount?: number;
		pendingOutCents?: number;
	} = $props();
	const liability = $derived(LIABILITY_TYPES.has(account.type));
</script>

<div
	class="card relative border border-base-300/60 bg-base-100 shadow-sm transition-transform active:scale-[0.99] motion-reduce:transition-none"
	data-testid="account-card"
>
	<div class="card-body flex-row items-center gap-3 p-4">
		<span
			class="flex h-11 w-11 shrink-0 items-center justify-center rounded-2xl text-white"
			style:background={account.color}
		>
			<Icon name={account.icon} size={22} />
		</span>
		<div class="min-w-0 flex-1">
			<a
				href="/accounts/{account.id}"
				class="block truncate font-medium after:absolute after:inset-0">{account.name}</a
			>
			<p class="truncate text-xs text-base-content/60">{account.institution}</p>
			{#if pendingCount > 0 || pendingOutCents > 0}
				<a
					href="/accounts/{account.id}?tab=transactions&filter=pending"
					class="relative z-10 mt-0.5 inline-flex min-h-8 items-center gap-1 rounded-full text-xs font-medium text-pending"
					data-testid="account-pending"
				>
					<span class="h-1.5 w-1.5 rounded-full bg-warning" aria-hidden="true"></span>
					{pendingCount > 0 ? `${pendingCount} pending · ` : ''}{signedMoney(-pendingOutCents)}
				</a>
			{/if}
		</div>
		<div class="text-right">
			<p class="font-semibold tabular {liability ? 'text-error' : ''}">
				{liability ? '−' : ''}{formatMoney(account.balanceCents)}
			</p>
			{#if liability}<p class="text-[0.65rem] text-base-content/50 uppercase">owed</p>{/if}
			{#if account.availableCents != null && account.availableCents !== account.balanceCents}
				<p class="text-[0.7rem] text-base-content/55 tabular">
					{formatMoney(account.availableCents)} available
				</p>
			{/if}
		</div>
		<Icon name="chevron-right" size={18} class="text-base-content/40" />
	</div>
</div>
