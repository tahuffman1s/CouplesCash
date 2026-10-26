<script lang="ts">
	import { untrack } from 'svelte';
	import type { RecurringItem } from '#lib/types.ts';
	import type { PageProps } from './$types';
	import Amount from '#lib/components/Amount.svelte';
	import AppBar from '#lib/components/AppBar.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import FoundBills from '#lib/components/FoundBills.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import NoHousehold from '#lib/components/NoHousehold.svelte';
	import QuickAddSheet, { quickAddForm } from '#lib/components/QuickAddSheet.svelte';
	import { FREQUENCY_SUFFIX } from '#lib/utils/labels.ts';
	import { formatDate, relativeDay, today } from '#lib/utils/dates.ts';
	import {
		BILL_FILTERS,
		billAccountLabel,
		billDate,
		groupBills,
		transferRoute,
		type BillFilter
	} from '#lib/utils/bills.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data, form }: PageProps = $props();

	let filter = $state<BillFilter>(untrack(() => (data.tab === 'once' ? 'once' : 'all')));
	const todayIso = today();
	const items = $derived(data.items ?? []);
	const groups = $derived(
		groupBills(items, {
			today: todayIso,
			paydayWeekday: data.household?.paydayWeekday ?? 5,
			filter
		})
	);
	const categoryById = $derived(new Map(data.categories.map((c) => [c.id, c])));
	const oneOffPaid = $derived(new Set(data.oneOffPaid));
	const oneOffStatus = (item: RecurringItem) =>
		oneOffPaid.has(item.id) ? 'Paid' : item.anchorDate < todayIso ? 'Late' : 'Planned';
	const signed = (i: RecurringItem) => (i.kind === 'income' ? 1 : -1) * i.amountCents;
	const icon = (i: RecurringItem) =>
		i.kind === 'income'
			? 'banknote'
			: i.kind === 'transfer'
				? 'arrow-left-right'
				: i.frequency === 'once'
					? 'calendar-check'
					: i.kind === 'subscription'
						? 'repeat'
						: 'receipt';

	let quickOpen = $state(false);
	const quick = $derived(quickAddForm(form));
	$effect(() => {
		if (data.added)
			untrack(() =>
				toasts.show(`${data.added} bill${data.added === 1 ? '' : 's'} added`, 'success')
			);
	});
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

<main class="flex flex-col gap-4 px-4 pt-2 pb-4">
	{#if !data.items}
		<NoHousehold />
	{:else}
		<FoundBills suggestions={data.suggestions} />

		<div
			class="-mx-4 flex gap-2 overflow-x-auto px-4 pb-1"
			role="radiogroup"
			aria-label="Filter by kind"
		>
			{#each BILL_FILTERS as chip (chip.value)}
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

		{#if items.length === 0}
			<EmptyState
				icon="receipt"
				title="No bills yet"
				body="Paste the list from your notes, or tap + and type “rent 1850 1st”."
			>
				<a href="/bills/paste" class="btn mt-3 min-h-11 rounded-full px-5 btn-primary"
					><Icon name="scroll-text" size={16} /> Paste from notes</a
				>
			</EmptyState>
		{:else if groups.length === 0}
			<p class="py-6 text-center text-sm text-base-content/60" data-testid="bills-none">
				Nothing here yet.
			</p>
		{:else}
			{#each groups as g (g.key)}
				<section aria-labelledby="g-{g.key}" data-testid="bill-group" data-group={g.key}>
					<h2
						id="g-{g.key}"
						class="mb-1 px-1 text-sm font-semibold tracking-wide text-base-content/70 uppercase"
					>
						{g.label}
					</h2>
					<ul
						class="card divide-y divide-base-300/60 border border-base-300/60 bg-base-100 shadow-sm"
						data-testid="bill-list"
					>
						{#each g.items as item (item.id)}
							{@const cat = item.categoryId ? categoryById.get(item.categoryId) : undefined}
							{@const date = billDate(item)}
							{@const acct =
								item.kind === 'transfer'
									? `→ ${transferRoute(item, data.accounts).split(' → ').pop()}`
									: billAccountLabel(item.accountId, data.accounts)}
							<li>
								<a
									href="/bills/{item.id}"
									class="flex min-h-16 items-center gap-3 px-4 py-2.5"
									data-testid="bill-row"
								>
									<span
										class="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl text-white"
										style:background={cat?.color ?? 'var(--color-neutral)'}
									>
										<Icon name={cat?.icon ?? icon(item)} size={18} />
									</span>
									<div class="min-w-0 flex-1">
										<p class="flex items-center gap-1.5">
											<span class="truncate font-medium">{item.name}</span>
											{#if item.frequency === 'once'}
												{@const status = oneOffStatus(item)}
												<span
													class="badge shrink-0 border-0 px-1.5 badge-xs text-[0.65rem] font-medium {status ===
													'Paid'
														? 'bg-success/15 text-success'
														: status === 'Late'
															? 'bg-error/15 text-error'
															: 'bg-base-200 text-base-content/70'}"
													data-testid="one-off-status">{status}</span
												>
											{/if}
										</p>
										<p class="truncate text-xs text-base-content/60">
											<span>{date ? `${relativeDay(date)} · ${formatDate(date)}` : 'Ended'}</span
											>{#if acct}<span aria-hidden="true" class="mx-1">·</span><span
													data-testid="bill-account">{acct}</span
												>{/if}{#if item.autoPay}<span aria-hidden="true" class="mx-1">·</span><span
													>Autopay</span
												>{/if}
										</p>
									</div>
									<div class="shrink-0 text-right">
										<Amount
											cents={item.kind === 'transfer' ? -item.amountCents : signed(item)}
											class="font-semibold"
										/>
										<p class="text-xs text-base-content/50">{FREQUENCY_SUFFIX[item.frequency]}</p>
									</div>
								</a>
							</li>
						{/each}
					</ul>
				</section>
			{/each}
		{/if}

		<a
			href="/bills/paste"
			class="flex min-h-12 items-center justify-center gap-2 rounded-2xl border border-dashed border-base-300 text-sm font-medium text-base-content/70"
			data-testid="paste-notes"
		>
			<Icon name="scroll-text" size={16} /> Paste from notes
		</a>
	{/if}
</main>

{#if data.items && data.household}
	<QuickAddSheet
		bind:open={quickOpen}
		categories={data.categories}
		accounts={data.accounts}
		defaultAccountId={data.accounts.find((a) => a.role === 'bills')?.id ??
			data.household.defaultAccountId}
		form={quick}
	/>
{/if}
