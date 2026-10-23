<script lang="ts">
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import NoHousehold from '#lib/components/NoHousehold.svelte';
	import GoalCard from '#lib/components/GoalCard.svelte';
	import { formatMoney } from '#lib/utils/money.ts';

	let { data }: PageProps = $props();
	const goals = $derived(data.goals ?? []);
	const saved = $derived(goals.reduce((s, g) => s + Math.min(g.currentCents, g.targetCents), 0));
	const target = $derived(goals.reduce((s, g) => s + g.targetCents, 0));
</script>

<svelte:head><title>Goals · CouplesCash</title></svelte:head>

<AppBar title="Savings goals" back="/accounts">
	{#snippet action()}
		{#if data.goals}
			<a href="/goals/new" class="btn min-h-11 rounded-full px-4 btn-primary btn-sm">
				<Icon name="plus" size={18} strokeWidth={2.5} /> Add
			</a>
		{/if}
	{/snippet}
</AppBar>

<main class="flex flex-col gap-4 px-4 pt-2">
	{#if !data.goals}
		<NoHousehold />
	{:else if goals.length === 0}
		<EmptyState
			icon="target"
			title="No goals yet"
			body="A holiday, an emergency fund, a new sofa. Link a goal to a savings account or track it by hand."
			cta="Create a goal"
			href="/goals/new"
		/>
	{:else}
		<section class="card border border-base-300/60 bg-base-100 shadow-sm">
			<div class="card-body flex-row items-center justify-between p-4">
				<div>
					<p class="text-xs text-base-content/60">Saved towards goals</p>
					<p class="text-2xl font-bold tabular">{formatMoney(saved, { whole: true })}</p>
				</div>
				<p class="text-sm text-base-content/60 tabular">
					of {formatMoney(target, { whole: true })}
				</p>
			</div>
		</section>
		<div class="flex flex-col gap-2">
			{#each goals as goal (goal.id)}
				<GoalCard {goal} />
			{/each}
		</div>
	{/if}
</main>
