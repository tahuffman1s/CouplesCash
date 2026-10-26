<script lang="ts">
	import type { PlanAccount } from '#lib/types.ts';
	import { formatMoney } from '#lib/utils/money.ts';
	import { accountLabel, orderAccounts } from '#lib/utils/plan.ts';

	/** v7: US Bank · Savings · Fun (Travis) · Fun (Jess), two by two; tap → that account's transactions. */
	let { accounts }: { accounts: PlanAccount[] } = $props();

	const shown = $derived(orderAccounts(accounts.filter((a) => a.role !== 'other')));
</script>

{#if shown.length}
	<nav class="grid grid-cols-2 gap-2" aria-label="Accounts" data-testid="accounts-strip">
		{#each shown as a (a.id)}
			<a
				href="/accounts/{a.id}?tab=transactions"
				class="flex min-h-14 flex-col justify-center rounded-2xl border border-base-300/70 bg-base-100 px-3 py-2 shadow-xs active:bg-base-200 {a.role ===
				'bills'
					? 'border-primary/40'
					: ''}"
				data-testid="strip-account"
				data-role={a.role}
			>
				<span class="truncate text-xs text-base-content/65">{accountLabel(a)}</span>
				<span class="truncate font-mono text-sm font-semibold tabular"
					>{formatMoney(a.availableCents ?? a.balanceCents)}</span
				>
			</a>
		{/each}
	</nav>
{/if}
