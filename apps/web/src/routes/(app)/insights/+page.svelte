<script lang="ts">
	import type { RecurringItem } from '#lib/types.ts';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import NoHousehold from '#lib/components/NoHousehold.svelte';
	import Donut from '#lib/charts/Donut.svelte';
	import KindBars from '#lib/charts/KindBars.svelte';
	import BudgetList from '#lib/components/BudgetList.svelte';
	import { formatMoney } from '#lib/utils/money.ts';
	import { addMonths, formatMonth } from '#lib/utils/dates.ts';
	import { FREQUENCY_SUFFIX } from '#lib/utils/labels.ts';

	let { data }: PageProps = $props();

	const s = $derived(data.summary);
	const first = $derived(`${data.month}-01`);
	let selected = $state<string | null>(null);
	const slices = $derived(
		(s?.byCategory ?? []).map((c) => ({
			id: c.categoryId ?? 'uncategorised',
			label: c.name,
			value: c.amountCents,
			color: c.color
		}))
	);
	const outflow = $derived(slices.reduce((a, c) => a + c.value, 0));
	const sortedCats = $derived(
		[...(s?.byCategory ?? [])].sort((a, b) => b.amountCents - a.amountCents)
	);
	const topSubs = $derived(
		[...data.subscriptions]
			.sort(
				(a: RecurringItem, b: RecurringItem) => b.monthlyEquivalentCents - a.monthlyEquivalentCents
			)
			.slice(0, 5)
	);
	const subsTotal = $derived(
		data.subscriptions.reduce((a: number, i: RecurringItem) => a + i.monthlyEquivalentCents, 0)
	);
	const hasData = $derived(
		!!s && (s.byKind.some((k) => k.count > 0) || s.byCategory.some((c) => c.amountCents > 0))
	);
</script>

<svelte:head><title>Insights · CouplesCash</title></svelte:head>

<AppBar title="Insights" back="/more">
	{#snippet action()}
		{#if s}
			<a
				href="/insights/report?m={data.month}"
				class="btn min-h-11 rounded-full btn-ghost px-3 btn-sm"
				data-testid="report-link"
			>
				<Icon name="file-text" size={18} /> Month report
			</a>
		{/if}
	{/snippet}
</AppBar>

<main class="flex flex-col gap-4 px-4 pt-2">
	{#if !s}
		<NoHousehold />
	{:else}
		<div class="flex items-center justify-between">
			<a
				href="?m={addMonths(first, -1).slice(0, 7)}"
				class="btn btn-circle min-h-11 min-w-11 btn-ghost"
				aria-label="Previous month"
			>
				<Icon name="chevron-left" size={22} />
			</a>
			<h2 class="font-semibold">{formatMonth(first)}</h2>
			<a
				href="?m={addMonths(first, 1).slice(0, 7)}"
				class="btn btn-circle min-h-11 min-w-11 btn-ghost"
				aria-label="Next month"
			>
				<Icon name="chevron-right" size={22} />
			</a>
		</div>

		{#if !hasData}
			<EmptyState
				icon="chart-pie"
				title="No insights yet"
				body="Add a few bills and subscriptions and this page shows where the money goes each month."
				cta="Add a bill"
				href="/bills/new"
			/>
		{:else}
			<section class="card border border-base-300/60 bg-base-100 shadow-sm" aria-labelledby="bycat">
				<div class="card-body gap-4 p-5">
					<div>
						<h2 id="bycat" class="font-semibold">Spending by category</h2>
						<p class="text-xs text-base-content/60">
							Monthly equivalent of every bill and subscription, plus this month's expenses
						</p>
					</div>
					{#if slices.length}
						<Donut
							{slices}
							size={220}
							thickness={24}
							title="Monthly outflow by category"
							bind:selected
						/>
					{/if}
					<ul class="divide-y divide-base-300/60 text-sm" aria-label="Category breakdown">
						{#each sortedCats as c (c.categoryId ?? 'uncategorised')}
							{@const id = c.categoryId ?? 'uncategorised'}
							<li
								class="flex items-center gap-3 py-2.5 {selected && selected !== id
									? 'opacity-50'
									: ''}"
							>
								<span
									class="h-3 w-3 shrink-0 rounded-full"
									style:background={c.color}
									aria-hidden="true"
								></span>
								<span class="flex-1 truncate">{c.name}</span>
								<span class="text-xs text-base-content/50">{c.count}</span>
								<span class="w-24 text-right font-medium tabular">{formatMoney(c.amountCents)}</span
								>
								<span class="w-10 text-right text-xs text-base-content/50 tabular">
									{outflow ? Math.round((c.amountCents / outflow) * 100) : 0}%
								</span>
							</li>
						{/each}
					</ul>
				</div>
			</section>

			{#if data.budgets}
				<section
					class="card border border-base-300/60 bg-base-100 shadow-sm"
					aria-labelledby="budgets"
				>
					<div class="card-body gap-3 p-5">
						<div>
							<h2 id="budgets" class="font-semibold">Budgets</h2>
							<p class="text-xs text-base-content/60">
								Monthly amount per category · tap a row to set or change it
							</p>
						</div>
						<BudgetList budgets={data.budgets} month={data.month} />
					</div>
				</section>
			{/if}

			<section
				class="card border border-base-300/60 bg-base-100 shadow-sm"
				aria-labelledby="bykind"
			>
				<div class="card-body gap-4 p-5">
					<h2 id="bykind" class="font-semibold">By kind</h2>
					<KindBars rows={s.byKind} />
					<div class="grid grid-cols-3 divide-x divide-base-300/60 text-center text-sm">
						<div>
							<p class="text-xs text-base-content/60">Income</p>
							<p class="font-semibold text-success tabular">
								{formatMoney(s.monthlyIncomeCents, { whole: true })}
							</p>
						</div>
						<div>
							<p class="text-xs text-base-content/60">Out</p>
							<p class="font-semibold tabular">
								{formatMoney(s.monthlyBillsCents + s.monthlySubscriptionsCents, { whole: true })}
							</p>
						</div>
						<div>
							<p class="text-xs text-base-content/60">Left over</p>
							<p
								class="font-semibold tabular {s.monthlyIncomeCents -
									s.monthlyBillsCents -
									s.monthlySubscriptionsCents <
								0
									? 'text-error'
									: ''}"
							>
								{formatMoney(
									s.monthlyIncomeCents - s.monthlyBillsCents - s.monthlySubscriptionsCents,
									{ whole: true }
								)}
							</p>
						</div>
					</div>
				</div>
			</section>

			{#if topSubs.length}
				<section
					class="card border border-base-300/60 bg-base-100 shadow-sm"
					aria-labelledby="subs"
				>
					<div class="card-body gap-2 p-5">
						<div class="flex items-baseline justify-between">
							<h2 id="subs" class="font-semibold">Top subscriptions</h2>
							<span class="text-sm text-base-content/60 tabular"
								>{formatMoney(subsTotal)}/mo total</span
							>
						</div>
						<ol class="divide-y divide-base-300/60 text-sm">
							{#each topSubs as sub, i (sub.id)}
								<li>
									<a href="/bills/{sub.id}" class="flex items-center gap-3 py-2.5">
										<span
											class="flex h-7 w-7 items-center justify-center rounded-full bg-base-200 text-xs font-semibold text-base-content/60"
											>{i + 1}</span
										>
										<span class="flex-1 truncate font-medium">{sub.name}</span>
										<span class="text-right tabular">
											{formatMoney(sub.monthlyEquivalentCents)}<span
												class="text-xs text-base-content/50">/mo</span
											>
										</span>
										{#if sub.frequency !== 'monthly'}
											<span class="w-14 text-right text-xs text-base-content/50"
												>{formatMoney(sub.amountCents, { whole: true })}{FREQUENCY_SUFFIX[
													sub.frequency
												]}</span
											>
										{/if}
									</a>
								</li>
							{/each}
						</ol>
					</div>
				</section>
			{/if}
		{/if}
	{/if}
</main>
