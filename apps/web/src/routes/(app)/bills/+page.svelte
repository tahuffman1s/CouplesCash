<script lang="ts">
	import type { Kind, RecurringItem } from '#lib/types.ts';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import NoHousehold from '#lib/components/NoHousehold.svelte';
	import { FREQUENCY_SUFFIX, KIND_LABEL_PLURAL } from '#lib/utils/labels.ts';
	import { formatMoney } from '#lib/utils/money.ts';
	import { formatDate, relativeDay } from '#lib/utils/dates.ts';

	let { data }: PageProps = $props();

	let filter = $state<Kind | 'all'>('all');
	const items = $derived(data.items ?? []);
	const visible = $derived(
		(filter === 'all' ? items : items.filter((i: RecurringItem) => i.kind === filter)).toSorted(
			(a: RecurringItem, b: RecurringItem) =>
				(a.nextDueDate ?? '9999').localeCompare(b.nextDueDate ?? '9999') ||
				a.name.localeCompare(b.name)
		)
	);
	const outflow = $derived(
		visible
			.filter((i: RecurringItem) => i.kind !== 'income')
			.reduce((s: number, i: RecurringItem) => s + i.monthlyEquivalentCents, 0)
	);
	const inflow = $derived(
		visible
			.filter((i: RecurringItem) => i.kind === 'income')
			.reduce((s: number, i: RecurringItem) => s + i.monthlyEquivalentCents, 0)
	);
	const categoryById = $derived(new Map(data.categories.map((c) => [c.id, c])));
	const chips: { value: Kind | 'all'; label: string }[] = [
		{ value: 'all', label: 'All' },
		{ value: 'bill', label: 'Bills' },
		{ value: 'subscription', label: 'Subscriptions' },
		{ value: 'income', label: 'Income' }
	];
</script>

<svelte:head><title>Bills · CouplesCash</title></svelte:head>

<AppBar title="Bills & subscriptions">
	{#snippet action()}
		{#if data.items}
			<a href="/bills/new" class="btn min-h-11 rounded-full px-4 btn-primary btn-sm">
				<Icon name="plus" size={18} strokeWidth={2.5} /> Add
			</a>
		{/if}
	{/snippet}
</AppBar>

<main class="flex flex-col gap-4 px-4 pt-2">
	{#if !data.items}
		<NoHousehold />
	{:else if items.length === 0}
		<EmptyState
			icon="receipt"
			title="Nothing recurring yet"
			body="Add rent, utilities, streaming, insurance, paychecks… anything that repeats. They'll show up on the calendar and in your monthly picture."
			cta="Add a bill"
			href="/bills/new"
		/>
	{:else}
		<div class="flex gap-2 overflow-x-auto pb-1" role="radiogroup" aria-label="Filter by kind">
			{#each chips as chip (chip.value)}
				<button
					type="button"
					role="radio"
					aria-checked={filter === chip.value}
					class="btn min-h-10 shrink-0 rounded-full px-4 btn-sm {filter === chip.value
						? 'btn-neutral'
						: 'border border-base-300 btn-ghost'}"
					onclick={() => (filter = chip.value)}
				>
					{chip.label}
				</button>
			{/each}
		</div>

		<section class="card border border-base-300/60 bg-base-100 shadow-sm">
			<div class="card-body flex-row items-center justify-between p-4">
				<div>
					<p class="text-xs text-base-content/60">Monthly equivalent</p>
					<p class="text-2xl font-bold tabular" data-testid="monthly-total">
						{formatMoney(filter === 'income' ? inflow : outflow)}
					</p>
				</div>
				{#if filter === 'all' && inflow > 0}
					<div class="text-right">
						<p class="text-xs text-base-content/60">Income</p>
						<p class="font-semibold text-success tabular">+{formatMoney(inflow)}</p>
					</div>
				{/if}
			</div>
		</section>

		{#if visible.length === 0}
			<p class="py-6 text-center text-sm text-base-content/60">
				No {KIND_LABEL_PLURAL[filter as Kind].toLowerCase()} yet.
			</p>
		{:else}
			<ul class="card divide-y divide-base-300/60 border border-base-300/60 bg-base-100 shadow-sm">
				{#each visible as item (item.id)}
					{@const cat = item.categoryId ? categoryById.get(item.categoryId) : undefined}
					<li>
						<a href="/bills/{item.id}" class="flex items-center gap-3 px-4 py-3">
							<span
								class="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl text-white"
								style:background={cat?.color ?? 'var(--color-neutral)'}
							>
								<Icon
									name={cat?.icon ?? (item.kind === 'income' ? 'banknote' : 'receipt')}
									size={18}
								/>
							</span>
							<div class="min-w-0 flex-1">
								<p class="truncate font-medium">{item.name}</p>
								<p class="truncate text-xs text-base-content/60">
									{#if item.nextDueDate}
										{relativeDay(item.nextDueDate)} · {formatDate(item.nextDueDate)}
									{:else}
										Ended
									{/if}
									{#if item.autoPay}
										· Auto-pay{/if}
								</p>
							</div>
							<div class="text-right">
								<p class="font-semibold tabular {item.kind === 'income' ? 'text-success' : ''}">
									{item.kind === 'income' ? '+' : ''}{formatMoney(item.amountCents)}
								</p>
								<p class="text-xs text-base-content/50">{FREQUENCY_SUFFIX[item.frequency]}</p>
							</div>
							<Icon name="chevron-right" size={18} class="text-base-content/40" />
						</a>
					</li>
				{/each}
			</ul>
		{/if}
	{/if}
</main>
