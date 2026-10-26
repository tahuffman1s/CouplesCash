<script lang="ts">
	import { untrack } from 'svelte';
	import type { Kind, RecurringItem } from '#lib/types.ts';
	import type { PageProps } from './$types';
	import Amount from '#lib/components/Amount.svelte';
	import AppBar from '#lib/components/AppBar.svelte';
	import CalendarView from '#lib/components/CalendarView.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import FoundBills from '#lib/components/FoundBills.svelte';
	import HelperText from '#lib/components/HelperText.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import NoHousehold from '#lib/components/NoHousehold.svelte';
	import QuickAddSheet, { quickAddForm } from '#lib/components/QuickAddSheet.svelte';
	import { FREQUENCY_SUFFIX, KIND_LABEL_PLURAL } from '#lib/utils/labels.ts';
	import { formatDate, relativeDay, today } from '#lib/utils/dates.ts';
	import { signedMoney } from '#lib/utils/money.ts';

	let { data, form }: PageProps = $props();

	type Filter = Kind | 'all' | 'once';
	let filter = $state<Filter>(untrack(() => (data.tab === 'once' ? 'once' : 'all')));
	const items = $derived(data.items ?? []);
	const oneOffs = $derived(items.filter((i: RecurringItem) => i.frequency === 'once'));
	const recurring = $derived(items.filter((i: RecurringItem) => i.frequency !== 'once'));
	const visible = $derived(
		(filter === 'once'
			? oneOffs
			: filter === 'all'
				? recurring
				: recurring.filter((i: RecurringItem) => i.kind === filter)
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
		{ value: 'once', label: 'One-offs' }
	];
	const oneOffPaid = $derived(new Set(data.oneOffPaid));
	const oneOffStatus = (item: RecurringItem) =>
		oneOffPaid.has(item.id) ? 'Paid' : item.anchorDate < today() ? 'Late' : 'Planned';
	const signed = (i: RecurringItem) => (i.kind === 'income' ? 1 : -1) * i.amountCents;
	const bankName = (i: RecurringItem) => i.bankNames?.[0] ?? i.payeeHint;

	let quickOpen = $state(false);
	const quick = $derived(quickAddForm(form));
</script>

<svelte:head><title>Bills · CouplesCash</title></svelte:head>

<AppBar title="Bills">
	{#snippet action()}
		{#if data.items}
			<button
				type="button"
				class="btn btn-circle min-h-11 min-w-11 btn-primary btn-sm"
				aria-label="Add something"
				data-testid="quick-add"
				onclick={() => (quickOpen = true)}
			>
				<Icon name="plus" size={22} strokeWidth={2.5} />
			</button>
		{/if}
	{/snippet}
</AppBar>

<main class="flex flex-col gap-4 px-4 pt-2">
	<HelperText
		id="bills"
		text="Everything that repeats, plus one-offs. Tap + and type it in a few words."
	/>
	{#if !data.items}
		<NoHousehold />
	{:else}
		<div class="join w-full" role="tablist" aria-label="Bills view">
			<a
				href="/bills"
				role="tab"
				aria-selected={data.view === 'list'}
				class="btn join-item min-h-11 flex-1 gap-1.5 {data.view === 'list'
					? 'btn-neutral'
					: 'border border-base-300 btn-ghost'}"
				data-sveltekit-noscroll><Icon name="list" size={16} /> List</a
			>
			<a
				href="/bills?view=calendar"
				role="tab"
				aria-selected={data.view === 'calendar'}
				class="btn join-item min-h-11 flex-1 gap-1.5 {data.view === 'calendar'
					? 'btn-neutral'
					: 'border border-base-300 btn-ghost'}"
				data-sveltekit-noscroll><Icon name="calendar-days" size={16} /> Calendar</a
			>
		</div>

		{#if data.view === 'calendar'}
			<CalendarView
				initialMonth={data.month}
				initialOccurrences={data.occurrences}
				initialDay={data.day}
				weekStart={data.household?.weekStart ?? 0}
				userId={data.user?.id ?? ''}
			/>
		{:else}
			<FoundBills suggestions={data.suggestions} />

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

			{#if filter === 'once'}
				{#if oneOffs.length === 0}
					<EmptyState
						icon="calendar-check"
						title="No one-offs planned"
						body="Something coming up that doesn't repeat: an oil change, a gift, a tax refund. Tap + and type “oil change 60 friday”."
					/>
				{:else}
					<ul
						class="card divide-y divide-base-300/60 border border-base-300/60 bg-base-100 shadow-sm"
						data-testid="one-offs"
					>
						{#each visible as item (item.id)}
							{@const cat = item.categoryId ? categoryById.get(item.categoryId) : undefined}
							{@const status = oneOffStatus(item)}
							<li>
								<a href="/bills/{item.id}" class="flex items-center gap-3 px-4 py-3">
									<span
										class="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl text-white"
										style:background={cat?.color ?? 'var(--color-neutral)'}
									>
										<Icon
											name={cat?.icon ?? (item.kind === 'income' ? 'banknote' : 'calendar-check')}
											size={18}
										/>
									</span>
									<div class="min-w-0 flex-1">
										<p class="flex items-center gap-1.5">
											<span class="truncate font-medium">{item.name}</span>
											<span
												class="badge shrink-0 border-0 px-1.5 badge-xs text-[0.65rem] font-medium {status ===
												'Paid'
													? 'bg-success/15 text-success'
													: status === 'Late'
														? 'bg-error/15 text-error'
														: 'bg-base-200 text-base-content/70'}"
												data-testid="one-off-status"
											>
												{status}
											</span>
										</p>
										<p class="truncate text-xs text-base-content/60">
											{relativeDay(item.anchorDate)} · {formatDate(item.anchorDate)}
											{#if bankName(item)}
												· bank: {bankName(item)}{/if}
										</p>
									</div>
									<Amount cents={signed(item)} class="font-semibold" />
									<Icon name="chevron-right" size={18} class="text-base-content/40" />
								</a>
							</li>
						{/each}
					</ul>
				{/if}
			{:else if recurring.length === 0}
				<EmptyState
					icon="receipt"
					title="Nothing recurring yet"
					body="Rent, utilities, streaming, insurance, paychecks: anything that repeats. Tap + and type “rent 1850 1st”."
				/>
			{:else}
				<section class="card border border-base-300/60 bg-base-100 shadow-sm">
					<div class="card-body flex-row items-center justify-between p-4">
						<div>
							<p class="text-xs text-base-content/60">
								{filter === 'income' ? 'Coming in each month' : 'Going out each month'}
							</p>
							<p class="text-2xl font-bold tabular" data-testid="monthly-total">
								{signedMoney(filter === 'income' ? inflow : -outflow)}
							</p>
						</div>
						{#if filter === 'all' && inflow > 0}
							<div class="text-right">
								<p class="text-xs text-base-content/60">Coming in</p>
								<p class="font-semibold text-success tabular">{signedMoney(inflow)}</p>
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
						data-testid="bill-list"
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
										<Amount cents={signed(item)} class="font-semibold" />
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
	{/if}
</main>

{#if data.items && data.household}
	<QuickAddSheet
		bind:open={quickOpen}
		categories={data.categories}
		accounts={data.accounts}
		defaultAccountId={data.household.defaultAccountId}
		form={quick}
	/>
{/if}
