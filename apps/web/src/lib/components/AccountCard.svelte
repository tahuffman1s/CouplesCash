<script lang="ts">
	import type { Account } from '#lib/types.ts';
	import { LIABILITY_TYPES } from '#lib/types.ts';
	import { formatMoney } from '#lib/utils/money.ts';
	import Icon from './Icon.svelte';

	let { account }: { account: Account } = $props();
	const liability = $derived(LIABILITY_TYPES.has(account.type));
</script>

<a
	href="/accounts/{account.id}"
	class="card border border-base-300/60 bg-base-100 shadow-sm transition-transform active:scale-[0.99] motion-reduce:transition-none"
>
	<div class="card-body flex-row items-center gap-3 p-4">
		<span
			class="flex h-11 w-11 shrink-0 items-center justify-center rounded-2xl text-white"
			style:background={account.color}
		>
			<Icon name={account.icon} size={22} />
		</span>
		<div class="min-w-0 flex-1">
			<p class="truncate font-medium">{account.name}</p>
			<p class="truncate text-xs text-base-content/60">{account.institution}</p>
		</div>
		<div class="text-right">
			<p class="font-semibold tabular {liability ? 'text-error' : ''}">
				{liability ? '−' : ''}{formatMoney(account.balanceCents)}
			</p>
			{#if liability}<p class="text-[0.65rem] text-base-content/50 uppercase">owed</p>{/if}
			{#if (account.pendingOutCents ?? 0) > 0}
				<p class="text-xs text-base-content/60 tabular" data-testid="account-pending">
					{formatMoney(account.pendingOutCents ?? 0)} pending
				</p>
			{/if}
		</div>
		<Icon name="chevron-right" size={18} class="text-base-content/40" />
	</div>
</a>
