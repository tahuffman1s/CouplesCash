<script lang="ts">
	import { invalidateAll } from '$app/navigation';
	import type { Occurrence } from '#lib/types.ts';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import NoHousehold from '#lib/components/NoHousehold.svelte';
	import OccurrenceRow from '#lib/components/OccurrenceRow.svelte';
	import PaydayCard from '#lib/components/PaydayCard.svelte';
	import { PAY_PERIOD_LABEL } from '#lib/types.ts';
	import { addDays, formatDate, today } from '#lib/utils/dates.ts';
	import { formatMoney } from '#lib/utils/money.ts';
	import { occurrenceKey, optimisticToggle, togglePaid } from '#lib/utils/occurrences.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data }: PageProps = $props();

	const view = $derived(data.view);
	let occurrences = $derived<Occurrence[]>(data.view?.occurrences ?? []);
	let busy = $state<string | null>(null);
	const isCurrent = $derived(!!view && view.start <= today() && today() <= view.end);

	async function onToggle(occ: Occurrence) {
		const key = occurrenceKey(occ);
		const i = occurrences.findIndex((o) => occurrenceKey(o) === key);
		if (i < 0 || busy) return;
		const previous = occurrences[i];
		occurrences = occurrences.with(i, optimisticToggle(occ, data.user?.id ?? ''));
		busy = key;
		try {
			occurrences = occurrences.with(i, await togglePaid(occ));
			void invalidateAll();
		} catch {
			occurrences = occurrences.with(i, previous);
			toasts.show('Could not update.', 'error');
		} finally {
			busy = null;
		}
	}
</script>

<svelte:head><title>Pay period · CouplesCash</title></svelte:head>

<AppBar title="Pay period" subtitle={view ? PAY_PERIOD_LABEL[view.period] : undefined} back="/more">
	{#snippet action()}
		<a
			href="/settings/household#payperiod"
			class="btn btn-circle min-h-11 min-w-11 btn-ghost btn-sm"
			aria-label="Pay period settings"
		>
			<Icon name="sliders-horizontal" size={20} />
		</a>
	{/snippet}
</AppBar>

<main class="flex flex-col gap-4 px-4 pt-2">
	{#if !data.household}
		<NoHousehold />
	{:else if !view}
		<EmptyState
			icon="calendar-clock"
			title="Set up your pay period"
			body="Tell us how often you're paid and a recent payday. We'll show what's due and what's safe to spend before the next one."
			cta="Set up pay period"
			href="/settings/household#payperiod"
		/>
	{:else}
		<div class="flex items-center justify-between">
			<a
				href="?date={addDays(view.start, -1)}"
				class="btn btn-circle min-h-11 min-w-11 btn-ghost"
				aria-label="Previous period"
			>
				<Icon name="chevron-left" size={22} />
			</a>
			<h2 class="text-sm font-semibold" data-testid="period-label">
				{formatDate(view.start)} – {formatDate(view.end)}
				{#if isCurrent}<span class="ml-1 badge badge-sm badge-secondary">now</span>{/if}
			</h2>
			<a
				href="?date={addDays(view.end, 1)}"
				class="btn btn-circle min-h-11 min-w-11 btn-ghost"
				aria-label="Next period"
			>
				<Icon name="chevron-right" size={22} />
			</a>
		</div>

		<PaydayCard {view} date={isCurrent ? today() : data.date} compact={false} />

		<section class="card border border-base-300/60 bg-base-100 shadow-sm" aria-labelledby="occ">
			<div class="card-body gap-1 p-5">
				<div class="flex items-baseline justify-between">
					<h2 id="occ" class="font-semibold">In this period</h2>
					{#if view.expensesCents > 0}
						<span class="text-xs text-base-content/60 tabular">
							+ {formatMoney(view.expensesCents)} in expenses
						</span>
					{/if}
				</div>
				{#if occurrences.length === 0}
					<p class="py-4 text-sm text-base-content/60">Nothing due between these paydays.</p>
				{:else}
					<ul class="divide-y divide-base-300/60">
						{#each occurrences as occ (occurrenceKey(occ))}
							<OccurrenceRow {occ} {onToggle} busy={busy === occurrenceKey(occ)} />
						{/each}
					</ul>
				{/if}
			</div>
		</section>
	{/if}
</main>
