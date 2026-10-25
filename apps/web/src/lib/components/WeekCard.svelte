<script lang="ts">
	import type { WeekView } from '#lib/types.ts';
	import { formatMoney } from '#lib/utils/money.ts';
	import { budgetStatus, weekRangeLabel } from '#lib/utils/week.ts';
	import Icon from './Icon.svelte';

	/** Dashboard "This week" card: the hero number of /week plus the planned / cleared / overdue count. */
	let { view }: { view: WeekView } = $props();

	const planned = $derived(view.days.flatMap((d) => d.planned));
	const cleared = $derived(planned.filter((o) => o.status === 'cleared').length);
	const overdue = $derived(
		planned.filter((o) => o.status === 'overdue').length + view.overdue.length
	);
	const budget = $derived(
		view.budget.overall
			? {
					row: view.budget.overall,
					st: budgetStatus(
						view.budget.overall.spentCents,
						view.budget.overall.budgetCents,
						formatMoney
					)
				}
			: null
	);
</script>

<a
	href="/week"
	class="card border border-base-300/60 bg-base-100 shadow-sm"
	aria-labelledby="thisweek"
	data-testid="week-card"
>
	<div class="card-body gap-2 p-5">
		<div class="flex items-start justify-between gap-3">
			<div>
				<h2 id="thisweek" class="text-xs font-medium tracking-wide text-base-content/60 uppercase">
					This week
				</h2>
				<p class="text-sm text-base-content/60">{weekRangeLabel(view.start, view.end)}</p>
			</div>
			<Icon name="chevron-right" size={18} class="mt-1 text-base-content/40" />
		</div>
		<div>
			<p class="text-xs text-base-content/60">Left at end of week</p>
			{#if view.leftAtEndCents === null}
				<p class="text-sm text-base-content/60">No balance history for this week</p>
			{:else}
				<p
					class="text-3xl leading-none font-bold tracking-tight tabular {view.leftAtEndCents < 0
						? 'text-error'
						: ''}"
					data-testid="week-card-left"
				>
					{formatMoney(view.leftAtEndCents)}
				</p>
			{/if}
		</div>
		<p class="text-sm text-base-content/70" data-testid="week-card-counts">
			{planned.length} planned · {cleared} cleared ·
			<span class={overdue > 0 ? 'font-medium text-error' : ''}>{overdue} overdue</span>
		</p>
		{#if budget}
			<p class="text-sm" data-testid="week-card-budget">
				<span class="text-base-content/70">Budget:</span>
				<span class="font-medium tabular {budget.st.over ? 'text-error' : 'text-success'}">
					{formatMoney(budget.row.spentCents)} of {formatMoney(budget.row.budgetCents)}
				</span>
				<span class="text-xs text-base-content/60">· {budget.st.label}</span>
			</p>
		{/if}
	</div>
</a>
