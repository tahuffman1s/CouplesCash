<script lang="ts">
	import { untrack } from 'svelte';
	import { replaceState } from '$app/navigation';
	import type { Expense, Kind, RecurringItem } from '#lib/types.ts';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import NoHousehold from '#lib/components/NoHousehold.svelte';
	import { FREQUENCY_SUFFIX, KIND_LABEL_PLURAL } from '#lib/utils/labels.ts';
	import { formatMoney } from '#lib/utils/money.ts';
	import { addMonths, dayHeading, formatDate, formatMonth, relativeDay } from '#lib/utils/dates.ts';
	import { apiFetch } from '#lib/utils/client.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data }: PageProps = $props();

	type Filter = Kind | 'all' | 'expenses';
	let filter = $state<Filter>(untrack(() => (data.tab === 'expenses' ? 'expenses' : 'all')));
	const items = $derived(data.items ?? []);
	const visible = $derived(
		(filter === 'all' || filter === 'expenses'
			? items
			: items.filter((i: RecurringItem) => i.kind === filter)
		).toSorted(
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
	const chips: { value: Filter; label: string }[] = [
		{ value: 'all', label: 'All' },
		{ value: 'bill', label: 'Bills' },
		{ value: 'subscription', label: 'Subscriptions' },
		{ value: 'income', label: 'Income' },
		{ value: 'expenses', label: 'Expenses' }
	];

	// --- expenses: month navigation through the proxy
	let month = $derived(data.month);
	let expenses = $derived<Expense[]>(data.expenses);
	let loading = $state(false);
	const expenseTotal = $derived(expenses.reduce((s, e) => s + e.amountCents, 0));
	const byDay = $derived.by(() => {
		const out: { day: string; list: Expense[]; total: number }[] = [];
		for (const e of [...expenses].sort((a, b) => b.spentOn.localeCompare(a.spentOn))) {
			const last = out[out.length - 1];
			if (last && last.day === e.spentOn) {
				last.list.push(e);
				last.total += e.amountCents;
			} else out.push({ day: e.spentOn, list: [e], total: e.amountCents });
		}
		return out;
	});

	async function go(delta: number) {
		const next = addMonths(`${month}-01`, delta).slice(0, 7);
		const prev = month;
		month = next;
		loading = true;
		try {
			expenses = await apiFetch<Expense[]>(`/expenses?month=${next}`);
			replaceState(`?tab=expenses&m=${next}`, {});
		} catch {
			month = prev;
			toasts.show('Could not load that month.', 'error');
		} finally {
			loading = false;
		}
	}
</script>

<svelte:head><title>Bills · CouplesCash</title></svelte:head>

<AppBar title={filter === 'expenses' ? 'Expenses' : 'Bills & subscriptions'}>
	{#snippet action()}
		{#if data.items}
			<a
				href={filter === 'expenses' ? '/expenses/new' : '/bills/new'}
				class="btn min-h-11 rounded-full px-4 btn-primary btn-sm"
			>
				<Icon name="plus" size={18} strokeWidth={2.5} /> Add
			</a>
		{/if}
	{/snippet}
</AppBar>

<main class="flex flex-col gap-4 px-4 pt-2">
	{#if !data.items}
		<NoHousehold />
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

		{#if filter === 'expenses'}
			<section class="card border border-base-300/60 bg-base-100 shadow-sm">
				<div class="card-body gap-2 p-4">
					<div class="flex items-center justify-between">
						<button
							type="button"
							class="btn btn-circle min-h-11 min-w-11 btn-ghost"
							aria-label="Previous month"
							onclick={() => go(-1)}
						>
							<Icon name="chevron-left" size={22} />
						</button>
						<h2 class="font-semibold" aria-live="polite" data-testid="expenses-month">
							{formatMonth(`${month}-01`)}
							{#if loading}<span class="loading ml-2 loading-xs loading-spinner align-middle"
								></span>{/if}
						</h2>
						<button
							type="button"
							class="btn btn-circle min-h-11 min-w-11 btn-ghost"
							aria-label="Next month"
							onclick={() => go(1)}
						>
							<Icon name="chevron-right" size={22} />
						</button>
					</div>
					<div class="flex items-baseline justify-between">
						<p class="text-xs text-base-content/60">
							{expenses.length} expense{expenses.length === 1 ? '' : 's'}
						</p>
						<p class="text-2xl font-bold tabular" data-testid="expenses-total">
							{formatMoney(expenseTotal)}
						</p>
					</div>
				</div>
			</section>

			{#if expenses.length === 0}
				<EmptyState
					icon="shopping-bag"
					title="No expenses this month"
					body="One-off spending – groceries, a dinner out, a repair – lives here and feeds your budgets and the pay-period view."
					cta="Add an expense"
					href="/expenses/new"
				/>
			{:else}
				{#each byDay as g (g.day)}
					<section aria-labelledby="exp-{g.day}" class={loading ? 'opacity-60' : ''}>
						<div class="mb-1 flex items-baseline justify-between px-1">
							<h2
								id="exp-{g.day}"
								class="text-sm font-semibold tracking-wide text-base-content/70 uppercase"
							>
								{dayHeading(g.day)}
							</h2>
							<span class="text-sm text-base-content/60 tabular">{formatMoney(g.total)}</span>
						</div>
						<ul
							class="card divide-y divide-base-300/60 border border-base-300/60 bg-base-100 shadow-sm"
						>
							{#each g.list as e (e.id)}
								{@const cat = e.categoryId ? categoryById.get(e.categoryId) : undefined}
								<li>
									<a href="/expenses/{e.id}" class="flex items-center gap-3 px-4 py-3">
										<span
											class="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl text-white"
											style:background={cat?.color ?? 'var(--color-neutral)'}
										>
											<Icon name={cat?.icon ?? 'shopping-bag'} size={18} />
										</span>
										<div class="min-w-0 flex-1">
											<p class="truncate font-medium">{e.name}</p>
											<p class="truncate text-xs text-base-content/60">
												{cat?.name ?? 'Uncategorised'}{#if e.deducted}
													· deducted{/if}
											</p>
										</div>
										<p class="font-semibold tabular">{formatMoney(e.amountCents)}</p>
										<Icon name="chevron-right" size={18} class="text-base-content/40" />
									</a>
								</li>
							{/each}
						</ul>
					</section>
				{/each}
			{/if}
		{:else if items.length === 0}
			<EmptyState
				icon="receipt"
				title="Nothing recurring yet"
				body="Add rent, utilities, streaming, insurance, paychecks… anything that repeats. They'll show up on the calendar and in your monthly picture."
				cta="Add a bill"
				href="/bills/new"
			/>
		{:else}
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
				<ul
					class="card divide-y divide-base-300/60 border border-base-300/60 bg-base-100 shadow-sm"
				>
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
											· Autopay{/if}
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
	{/if}
</main>
