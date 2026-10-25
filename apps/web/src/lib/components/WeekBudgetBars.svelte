<script lang="ts">
	import type { WeekBudget, WeekBudgetRow } from '#lib/types.ts';
	import { formatMoney } from '#lib/utils/money.ts';
	import { budgetStatus, cappedPercent } from '#lib/utils/week.ts';
	import WeekHistoryBars from '#lib/charts/WeekHistoryBars.svelte';
	import Icon from './Icon.svelte';

	let {
		budget,
		currentStart,
		history = true,
		onEdit
	}: {
		budget: WeekBudget;
		/** week being viewed, for the history labels */
		currentStart?: string;
		/** render the 8-week history chart below the bars */
		history?: boolean;
		/** "Set weekly budgets" callback; omitted → no button (Insights links to /week instead) */
		onEdit?: () => void;
	} = $props();

	const hasAny = $derived(budget.overall !== null || budget.categories.length > 0);
	const statusOf = (r: WeekBudgetRow) => budgetStatus(r.spentCents, r.budgetCents, formatMoney);
</script>

{#snippet bar(row: WeekBudgetRow, overall: boolean)}
	{@const st = statusOf(row)}
	{@const pct = cappedPercent(row.spentCents, row.budgetCents)}
	{@const fill = st.over ? 'var(--color-error)' : overall ? 'var(--color-primary)' : row.color}
	<li
		class="flex flex-col gap-1.5 {overall ? 'pb-3' : 'py-3'}"
		data-testid={overall ? 'week-budget-overall' : 'week-budget-row'}
		data-category={row.name}
	>
		<div class="flex items-center gap-2 text-sm">
			{#if overall}
				<span class="flex-1 font-semibold">Weekly target</span>
			{:else}
				<span
					class="h-2.5 w-2.5 shrink-0 rounded-full"
					style:background={row.color}
					aria-hidden="true"
				></span>
				<span class="flex-1 truncate font-medium">{row.name}</span>
			{/if}
			<span class="tabular {st.over ? 'font-semibold text-error' : 'text-base-content/80'}">
				{formatMoney(row.spentCents)}
				<span class="text-base-content/50"> / {formatMoney(row.budgetCents)}</span>
			</span>
		</div>
		<!-- meter: ≤ 24px thick, track = lighter step of the fill; overflow shows as a hatched tail -->
		<div
			class="relative h-2.5 w-full rounded-full {st.over ? 'ring-2 ring-error/30' : ''}"
			style:background="color-mix(in oklab, {fill} 18%, transparent)"
			role="meter"
			aria-valuemin="0"
			aria-valuemax={row.budgetCents}
			aria-valuenow={row.spentCents}
			aria-label="{overall ? 'Weekly target' : row.name}: {formatMoney(
				row.spentCents
			)} of {formatMoney(row.budgetCents)}{st.over ? ', over budget' : ''}"
		>
			<div
				class="h-full rounded-full transition-[width] duration-500 motion-reduce:transition-none"
				style:width="{pct}%"
				style:background={fill}
			></div>
		</div>
		<p
			class="text-xs {st.tone === 'error'
				? 'font-medium text-error'
				: st.tone === 'success'
					? 'font-medium text-success'
					: 'text-base-content/60'}"
			data-testid={overall ? 'week-budget-overall-status' : 'week-budget-status'}
		>
			{st.label.charAt(0).toUpperCase()}{st.label.slice(1)}
		</p>
	</li>
{/snippet}

<div class="flex flex-col gap-2" data-testid="week-budget">
	{#if !hasAny}
		<div class="flex flex-col items-center gap-2 py-4 text-center" data-testid="week-budget-empty">
			<span
				class="flex h-12 w-12 items-center justify-center rounded-full bg-primary/10 text-primary"
			>
				<Icon name="target" size={22} />
			</span>
			<p class="text-sm font-medium">No weekly budgets yet</p>
			<p class="max-w-xs text-xs text-base-content/60">
				Set one overall weekly target, or an amount per category, and this card tells you if you're
				under or over before the week is out.
			</p>
			{#if onEdit}
				<button
					type="button"
					class="btn mt-1 min-h-11 rounded-full btn-primary btn-sm"
					onclick={onEdit}
				>
					Set weekly budgets
				</button>
			{:else}
				<a href="/week" class="btn mt-1 min-h-11 rounded-full btn-primary btn-sm"
					>Set weekly budgets</a
				>
			{/if}
		</div>
	{:else}
		<ul class="flex flex-col">
			{#if budget.overall}
				{@render bar(budget.overall, true)}
			{/if}
			{#if budget.categories.length}
				<ul
					class="divide-y divide-base-300/60 {budget.overall ? 'border-t border-base-300/60' : ''}"
				>
					{#each budget.categories as row (row.categoryId)}
						{@render bar(row, false)}
					{/each}
				</ul>
				<li
					class="flex items-baseline justify-between border-t border-base-300/60 pt-3 text-sm"
					data-testid="week-budget-total"
				>
					<span class="font-semibold">Categories total</span>
					<span
						class="tabular {budget.totalSpentCents > budget.totalBudgetCents
							? 'font-semibold text-error'
							: ''}"
					>
						{formatMoney(budget.totalSpentCents)}
						<span class="text-base-content/50">/ {formatMoney(budget.totalBudgetCents)}</span>
					</span>
				</li>
			{/if}
		</ul>
		{#if onEdit}
			<button
				type="button"
				class="btn min-h-11 w-full rounded-full border border-base-300 btn-ghost btn-sm"
				onclick={onEdit}
			>
				<Icon name="pencil" size={16} /> Set weekly budgets
			</button>
		{:else}
			<a href="/week" class="link self-end text-sm link-primary no-underline"
				>Edit on the week view</a
			>
		{/if}
		{#if history && budget.history.length}
			<div class="mt-2">
				<p class="mb-1 text-xs font-medium text-base-content/70">Last 8 weeks</p>
				<WeekHistoryBars history={budget.history} {currentStart} />
			</div>
		{/if}
	{/if}
</div>
