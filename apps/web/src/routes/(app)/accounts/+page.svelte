<script lang="ts">
	import { invalidateAll } from '$app/navigation';
	import type { Account, AccountType, InboxItem, PendingGroup } from '#lib/types.ts';
	import { ACCOUNT_TYPES, LIABILITY_TYPES } from '#lib/types.ts';
	import { ACCOUNT_TYPE_LABEL } from '#lib/utils/labels.ts';
	import { formatMoney, signedMoney } from '#lib/utils/money.ts';
	import { formatMonth, today } from '#lib/utils/dates.ts';
	import { flowLabel } from '#lib/utils/transactions.ts';
	import type { PageProps } from './$types';
	import AccountCard from '#lib/components/AccountCard.svelte';
	import AccountTxRow from '#lib/components/AccountTxRow.svelte';
	import AppBar from '#lib/components/AppBar.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import GoalCard from '#lib/components/GoalCard.svelte';
	import HelperText from '#lib/components/HelperText.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import NoHousehold from '#lib/components/NoHousehold.svelte';
	import TransactionSheet from '#lib/components/TransactionSheet.svelte';

	let { data }: PageProps = $props();

	const accounts = $derived(data.accounts ?? []);
	const groups = $derived(
		ACCOUNT_TYPES.map((type: AccountType) => {
			const items = accounts.filter((a: Account) => a.type === type);
			return {
				type,
				label: ACCOUNT_TYPE_LABEL[type],
				items,
				total: items.reduce((s: number, a: Account) => s + a.balanceCents, 0)
			};
		}).filter((g) => g.items.length > 0)
	);
	const assets = $derived(
		accounts.filter((a) => !LIABILITY_TYPES.has(a.type)).reduce((s, a) => s + a.balanceCents, 0)
	);
	const owed = $derived(
		accounts.filter((a) => LIABILITY_TYPES.has(a.type)).reduce((s, a) => s + a.balanceCents, 0)
	);
	const pendingBy = $derived(
		new Map((data.pending?.groups ?? []).map((g) => [g.accountId ?? '', g]))
	);
	const pendingGroups = $derived((data.pending?.groups ?? []).filter((g) => g.pendingCount > 0));
	const anyLinked = $derived(accounts.some((a) => a.linked));
	const moved = $derived(data.transfers);

	const pendingLine = (g: PendingGroup) =>
		`${g.pendingCount} pending` +
		(g.availableCents !== null
			? ` · balance ${formatMoney(g.balanceCents)} → available ${formatMoney(g.availableCents)}`
			: '');

	// pending rows open the shared transaction sheet
	let sheet = $state(false);
	let selected = $state<InboxItem | null>(null);
	let dirty = $state(false);
	function open(tx: InboxItem) {
		selected = tx;
		sheet = true;
	}
	$effect(() => {
		if (!sheet && dirty) {
			dirty = false;
			void invalidateAll();
		}
	});
</script>

<svelte:head><title>Money · CouplesCash</title></svelte:head>

<AppBar title="Money">
	{#snippet action()}
		{#if data.accounts}
			<a href="/accounts/new" class="btn min-h-11 rounded-full px-4 btn-primary btn-sm">
				<Icon name="plus" size={18} strokeWidth={2.5} /> Account
			</a>
		{/if}
	{/snippet}
</AppBar>

<main class="flex flex-col gap-4 px-4 pt-2">
	<HelperText
		id="money"
		text="Every account, what's still pending, your goals and money moved between accounts."
	/>
	{#if !data.accounts}
		<NoHousehold />
	{:else}
		{#if accounts.length === 0}
			<EmptyState
				icon="wallet"
				title="No accounts yet"
				body="Add the accounts you both care about: checking, savings, credit cards, loans. Type balances in, or link a bank to keep them up to date."
				cta="Add your first account"
				href="/accounts/new"
			>
				<a href="/settings/banks" class="btn mt-1 min-h-11 btn-ghost btn-sm">Or link a bank</a>
			</EmptyState>
		{:else}
			<!-- Net worth -->
			<section
				class="card border-0 bg-gradient-to-br from-primary to-primary/80 text-primary-content shadow-md"
				aria-labelledby="networth-h"
			>
				<div class="card-body gap-3 p-5">
					<p id="networth-h" class="text-sm font-medium text-primary-content/85">Net worth</p>
					<p class="text-5xl leading-none font-bold tracking-tight" data-testid="net-worth">
						{formatMoney(assets - owed)}
					</p>
					<dl class="grid grid-cols-2 gap-2 text-sm">
						<div class="rounded-xl bg-primary-content/15 px-3 py-2">
							<dt class="text-xs text-primary-content/80">You have</dt>
							<dd class="font-semibold tabular" data-testid="assets-total">
								{formatMoney(assets)}
							</dd>
						</div>
						<div class="rounded-xl bg-primary-content/15 px-3 py-2">
							<dt class="text-xs text-primary-content/80">You owe</dt>
							<dd class="font-semibold tabular" data-testid="owed-total">{formatMoney(owed)}</dd>
						</div>
					</dl>
				</div>
			</section>
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
						{@const p = pendingBy.get(account.id)}
						<AccountCard
							{account}
							pendingCount={p?.pendingCount ?? 0}
							pendingOutCents={p?.pendingOutCents ?? account.pendingOutCents ?? 0}
						/>
					{/each}
				</div>
			</section>
		{/each}

		<!-- Pending, by account -->
		{#if anyLinked || pendingGroups.length}
			<section id="pending" class="scroll-mt-20" aria-labelledby="pending-h" data-testid="pending">
				<div class="mb-2 flex items-baseline justify-between px-1">
					<h2
						id="pending-h"
						class="text-sm font-semibold tracking-wide text-base-content/70 uppercase"
					>
						Pending
					</h2>
					{#if data.pending && data.pending.count > 0}
						<span class="text-sm text-base-content/60 tabular" data-testid="pending-total"
							>{data.pending.count} · {signedMoney(-data.pending.totalOutCents)}</span
						>
					{/if}
				</div>
				{#if pendingGroups.length === 0}
					<p class="rounded-2xl bg-base-200/60 px-4 py-3 text-sm text-base-content/65">
						Nothing pending. Everything from your banks has posted.
					</p>
				{:else}
					<div class="flex flex-col gap-3">
						{#each pendingGroups as g (`${g.accountId ?? 'none'}:${g.accountName}`)}
							<div
								class="card border border-warning/40 bg-base-100 shadow-sm"
								data-testid="pending-group"
								data-account={g.accountName}
							>
								<div class="card-body gap-1 p-4">
									<div class="flex items-baseline justify-between gap-2">
										{#if g.accountId}
											<a href="/accounts/{g.accountId}" class="truncate font-semibold"
												>{g.accountName}</a
											>
										{:else}
											<h3 class="truncate font-semibold">{g.accountName}</h3>
										{/if}
										<span
											class="text-sm font-semibold text-error tabular"
											data-testid="pending-subtotal">{signedMoney(-g.pendingOutCents)}</span
										>
									</div>
									<p class="text-xs text-base-content/60" data-testid="pending-balance">
										{pendingLine(g)}
									</p>
									<ul class="divide-y divide-base-300/60">
										{#each g.items as tx (tx.id)}
											<AccountTxRow {tx} onopen={(t) => open(t)} />
										{/each}
									</ul>
								</div>
							</div>
						{/each}
					</div>
				{/if}
			</section>
		{/if}

		<!-- Goals -->
		<section id="goals" class="scroll-mt-20" aria-labelledby="goals-h" data-testid="goals-section">
			<div class="mb-2 flex items-baseline justify-between px-1">
				<h2 id="goals-h" class="text-sm font-semibold tracking-wide text-base-content/70 uppercase">
					Goals
				</h2>
				{#if data.goals.length}
					<a href="/goals/new" class="link text-sm link-primary no-underline">Add a goal</a>
				{/if}
			</div>
			{#if data.goals.length === 0}
				<a
					href="/goals/new"
					class="card border border-dashed border-base-300 bg-base-100/60 text-sm shadow-none"
				>
					<div class="card-body min-h-14 flex-row items-center gap-3 px-4 py-3">
						<Icon name="target" size={20} class="text-secondary" />
						<span class="flex-1">Saving for something? Set a goal and watch the ring fill up.</span>
						<Icon name="chevron-right" size={18} class="text-base-content/40" />
					</div>
				</a>
			{:else}
				<div class="flex flex-col gap-2">
					{#each data.goals as goal (goal.id)}
						<GoalCard {goal} />
					{/each}
				</div>
			{/if}
		</section>

		<!-- Moved between accounts -->
		{#if moved && (moved.totalCents > 0 || moved.flows.length > 0)}
			<section aria-labelledby="moved-h" data-testid="moved">
				<div class="mb-2 flex items-baseline justify-between px-1">
					<h2
						id="moved-h"
						class="text-sm font-semibold tracking-wide text-base-content/70 uppercase"
					>
						Moved between accounts · {formatMonth(today())}
					</h2>
				</div>
				<a href="/transfers" class="card border border-base-300/60 bg-base-100 shadow-sm">
					<div class="card-body gap-2 p-4">
						<div class="flex items-baseline justify-between">
							<p class="text-sm text-base-content/65">Never counted as spending</p>
							<p class="font-semibold tabular" data-testid="moved-total">
								{formatMoney(moved.totalCents)}
							</p>
						</div>
						<ul class="flex flex-col gap-1 text-sm">
							{#each moved.flows.slice(0, 3) as fl (`${fl.fromName}→${fl.toName}`)}
								<li class="flex items-center justify-between gap-2">
									<span class="truncate">{flowLabel(fl)}</span>
									<span class="text-base-content/70 tabular">{formatMoney(fl.totalCents)}</span>
								</li>
							{/each}
						</ul>
						<p class="flex items-center gap-1 text-sm font-medium text-primary">
							See transfers <Icon name="chevron-right" size={16} />
						</p>
					</div>
				</a>
			</section>
		{/if}
	{/if}
</main>

<TransactionSheet
	bind:open={sheet}
	item={selected}
	categories={data.categories}
	onchange={(next) => {
		selected = next;
		dirty = true;
	}}
/>
