<script lang="ts">
	import type { Goal } from '#lib/types.ts';
	import { formatMoney } from '#lib/utils/money.ts';
	import { formatDate } from '#lib/utils/dates.ts';
	import ProgressRing from '#lib/charts/ProgressRing.svelte';
	import Icon from './Icon.svelte';

	let { goal }: { goal: Goal } = $props();
	const reached = $derived(goal.currentCents >= goal.targetCents);
</script>

<a
	href="/goals/{goal.id}"
	class="card border border-base-300/60 bg-base-100 shadow-sm transition-transform active:scale-[0.99] motion-reduce:transition-none"
	data-testid="goal-card"
>
	<div class="card-body flex-row items-center gap-3 p-4">
		<ProgressRing
			percent={goal.percent}
			color={goal.color}
			size={60}
			thickness={6}
			label="{goal.name} {Math.round(goal.percent)}% saved"
		>
			<Icon name={goal.icon} size={20} class="text-base-content/80" />
		</ProgressRing>
		<div class="min-w-0 flex-1">
			<p class="truncate font-medium">{goal.name}</p>
			<p class="text-xs text-base-content/60 tabular">
				<span class="font-semibold text-base-content"
					>{formatMoney(goal.currentCents, { whole: true })}</span
				>
				of {formatMoney(goal.targetCents, { whole: true })}
				<span class="text-base-content/40">·</span>
				<span data-testid="goal-percent">{Math.round(goal.percent)}%</span>
			</p>
			<p class="truncate text-xs text-base-content/60">
				{#if reached}
					Reached 🎉
				{:else if goal.monthlyNeededCents !== null && goal.targetDate}
					Needs {formatMoney(goal.monthlyNeededCents, { whole: true })}/mo by {formatDate(
						goal.targetDate
					)}
				{:else if goal.projectedDate}
					On track for {formatDate(goal.projectedDate)}
				{:else if goal.accountName}
					Linked to {goal.accountName}
				{:else}
					Tracked by hand
				{/if}
			</p>
		</div>
		<Icon name="chevron-right" size={18} class="text-base-content/40" />
	</div>
</a>
