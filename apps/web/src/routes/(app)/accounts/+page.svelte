<script lang="ts">
	import type { Account, AccountType } from '#lib/types.ts';
	import { ACCOUNT_TYPES, LIABILITY_TYPES } from '#lib/types.ts';
	import { ACCOUNT_TYPE_LABEL } from '#lib/utils/labels.ts';
	import { formatMoney } from '#lib/utils/money.ts';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import NoHousehold from '#lib/components/NoHousehold.svelte';
	import AccountCard from '#lib/components/AccountCard.svelte';
	import GoalCard from '#lib/components/GoalCard.svelte';

	let { data }: PageProps = $props();

	const groups = $derived.by(() => {
		const accounts = data.accounts ?? [];
		return ACCOUNT_TYPES.map((type: AccountType) => {
			const items = accounts.filter((a: Account) => a.type === type);
			return {
				type,
				label: ACCOUNT_TYPE_LABEL[type],
				items,
				total: items.reduce((s: number, a: Account) => s + a.balanceCents, 0)
			};
		}).filter((g) => g.items.length > 0);
	});
	const assets = $derived(
		(data.accounts ?? [])
			.filter((a: Account) => !LIABILITY_TYPES.has(a.type))
			.reduce((s: number, a: Account) => s + a.balanceCents, 0)
	);
	const owed = $derived(
		(data.accounts ?? [])
			.filter((a: Account) => LIABILITY_TYPES.has(a.type))
			.reduce((s: number, a: Account) => s + a.balanceCents, 0)
	);
</script>

<svelte:head><title>Accounts · CouplesCash</title></svelte:head>

<AppBar title="Accounts">
	{#snippet action()}
		{#if data.accounts}
			<a href="/accounts/new" class="btn min-h-11 rounded-full px-4 btn-primary btn-sm">
				<Icon name="plus" size={18} strokeWidth={2.5} /> Add
			</a>
		{/if}
	{/snippet}
</AppBar>

<main class="flex flex-col gap-4 px-4 pt-2">
	{#if !data.accounts}
		<NoHousehold />
	{:else if data.accounts.length === 0 && data.goals.length === 0}
		<EmptyState
			icon="wallet"
			title="No accounts yet"
			body="Add the accounts you both care about — checking, savings, credit cards, loans. Balances are updated by hand, no bank login needed."
			cta="Add your first account"
			href="/accounts/new"
		/>
	{:else}
		<section class="grid grid-cols-2 gap-3" aria-label="Totals">
			<div class="card border border-base-300/60 bg-base-100 shadow-sm">
				<div class="card-body p-4">
					<p class="text-xs text-base-content/60">Assets</p>
					<p class="text-xl font-semibold tabular" data-testid="assets-total">
						{formatMoney(assets)}
					</p>
				</div>
			</div>
			<div class="card border border-base-300/60 bg-base-100 shadow-sm">
				<div class="card-body p-4">
					<p class="text-xs text-base-content/60">Owed</p>
					<p class="text-xl font-semibold text-error tabular">{formatMoney(owed)}</p>
				</div>
			</div>
			<div class="card col-span-2 border border-base-300/60 bg-base-100 shadow-sm">
				<div class="card-body flex-row items-center justify-between p-4">
					<p class="text-sm text-base-content/60">Net worth</p>
					<p class="text-2xl font-bold tabular" data-testid="net-worth">
						{formatMoney(assets - owed)}
					</p>
				</div>
			</div>
		</section>

		{#if data.accounts.length === 0}
			<EmptyState
				icon="wallet"
				title="No accounts yet"
				body="Add checking, savings, credit cards, loans. Balances are updated by hand."
				cta="Add your first account"
				href="/accounts/new"
			/>
		{/if}
		{#each groups as g (g.type)}
			<section aria-labelledby="group-{g.type}">
				<div class="mb-2 flex items-baseline justify-between px-1">
					<h2
						id="group-{g.type}"
						class="text-sm font-semibold tracking-wide text-base-content/70 uppercase"
					>
						{g.label}
					</h2>
					<span class="text-sm text-base-content/60 tabular">{formatMoney(g.total)}</span>
				</div>
				<div class="flex flex-col gap-2">
					{#each g.items as account (account.id)}
						<AccountCard {account} />
					{/each}
				</div>
			</section>
		{/each}

		<section aria-labelledby="goals" data-testid="goals-section">
			<div class="mb-2 flex items-baseline justify-between px-1">
				<h2 id="goals" class="text-sm font-semibold tracking-wide text-base-content/70 uppercase">
					Goals
				</h2>
				<a
					href={data.goals.length ? '/goals' : '/goals/new'}
					class="link text-sm link-primary no-underline"
				>
					{data.goals.length ? 'See all' : 'Add a goal'}
				</a>
			</div>
			{#if data.goals.length === 0}
				<a
					href="/goals/new"
					class="card border border-dashed border-base-300 bg-base-100/60 text-sm shadow-none"
				>
					<div class="card-body flex-row items-center gap-3 px-4 py-3">
						<Icon name="target" size={20} class="text-secondary" />
						<span class="flex-1">Saving for something? Set a goal and watch the ring fill up.</span>
						<Icon name="chevron-right" size={18} class="text-base-content/40" />
					</div>
				</a>
			{:else}
				<div class="flex flex-col gap-2">
					{#each data.goals.slice(0, 3) as goal (goal.id)}
						<GoalCard {goal} />
					{/each}
				</div>
			{/if}
		</section>
	{/if}
</main>
