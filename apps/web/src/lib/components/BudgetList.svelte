<script lang="ts">
	import type { BudgetRow, BudgetsResponse } from '#lib/types.ts';
	import { formatMoney, parseMoney, centsToInput, currencySymbol } from '#lib/utils/money.ts';
	import { apiFetch } from '#lib/utils/client.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';
	import BudgetBar from '#lib/charts/BudgetBar.svelte';
	import Icon from './Icon.svelte';

	let { budgets, month }: { budgets: BudgetsResponse; month: string } = $props();

	// Writable derived: resets when the server data changes, patched in place after a PUT/DELETE
	let rows = $derived<BudgetRow[]>(budgets.rows);
	let editing = $state<string | null>(null);
	let amount = $state('');
	let busy = $state(false);
	let showUnbudgeted = $state(false);

	const budgeted = $derived(rows.filter((r) => r.budgetCents !== null));
	const unbudgeted = $derived(rows.filter((r) => r.budgetCents === null));
	const totalBudget = $derived(budgeted.reduce((s, r) => s + (r.budgetCents ?? 0), 0));
	const totalSpent = $derived(budgeted.reduce((s, r) => s + r.spentCents, 0));

	function startEdit(row: BudgetRow) {
		editing = row.categoryId;
		amount = row.budgetCents !== null ? centsToInput(row.budgetCents) : '';
	}

	async function save(row: BudgetRow) {
		const cents = parseMoney(amount);
		if (cents === null || cents < 0) {
			toasts.show('Enter an amount like 400', 'error');
			return;
		}
		busy = true;
		try {
			const updated = await apiFetch<BudgetRow>(`/budgets/${row.categoryId}?month=${month}`, {
				method: 'PUT',
				body: JSON.stringify({ amountCents: cents })
			});
			rows = rows.map((r) => (r.categoryId === row.categoryId ? { ...r, ...updated } : r));
			editing = null;
			toasts.show(`${row.name} budget set`, 'success');
		} catch {
			toasts.show('Could not save the budget.', 'error');
		} finally {
			busy = false;
		}
	}

	async function clear(row: BudgetRow) {
		busy = true;
		try {
			await apiFetch(`/budgets/${row.categoryId}`, { method: 'DELETE' });
			rows = rows.map((r) =>
				r.categoryId === row.categoryId
					? { ...r, budgetCents: null, remainingCents: null, percent: null }
					: r
			);
			editing = null;
			toasts.show(`${row.name} budget cleared`, 'info');
		} catch {
			toasts.show('Could not clear the budget.', 'error');
		} finally {
			busy = false;
		}
	}
</script>

{#snippet rowView(row: BudgetRow)}
	{@const over = row.budgetCents !== null && row.spentCents > row.budgetCents}
	<li class="py-3" data-testid="budget-row" data-category={row.name}>
		{#if editing === row.categoryId}
			<form
				class="flex items-center gap-2"
				onsubmit={(e) => {
					e.preventDefault();
					void save(row);
				}}
				aria-label="Budget for {row.name}"
			>
				<span
					class="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg text-white"
					style:background={row.color}
				>
					<Icon name={row.icon} size={14} />
				</span>
				<label class="input-bordered input flex min-h-11 flex-1 items-center gap-2">
					<span class="text-base-content/50">{currencySymbol()}</span>
					<input
						type="text"
						inputmode="decimal"
						class="grow tabular"
						placeholder="Monthly budget"
						aria-label="Monthly budget for {row.name}"
						bind:value={amount}
					/>
				</label>
				<button class="btn min-h-11 rounded-xl btn-secondary" disabled={busy}>Set</button>
				{#if row.budgetCents !== null}
					<button
						type="button"
						class="btn btn-circle min-h-11 min-w-11 btn-ghost"
						aria-label="Clear budget for {row.name}"
						disabled={busy}
						onclick={() => clear(row)}
					>
						<Icon name="circle-minus" size={18} />
					</button>
				{/if}
				<button
					type="button"
					class="btn btn-circle min-h-11 min-w-11 btn-ghost"
					aria-label="Cancel"
					onclick={() => (editing = null)}
				>
					<Icon name="x" size={18} />
				</button>
			</form>
		{:else}
			<button
				type="button"
				class="flex w-full flex-col gap-1.5 rounded-lg text-left"
				onclick={() => startEdit(row)}
				aria-label="{row.name}: {row.budgetCents === null
					? 'set a budget'
					: `${formatMoney(row.spentCents)} of ${formatMoney(row.budgetCents)}`}"
			>
				<span class="flex items-center gap-2 text-sm">
					<span
						class="h-2.5 w-2.5 shrink-0 rounded-full"
						style:background={row.color}
						aria-hidden="true"
					></span>
					<span class="flex-1 truncate font-medium">{row.name}</span>
					{#if row.budgetCents === null}
						<span class="text-xs text-base-content/60 tabular">{formatMoney(row.spentCents)}</span>
						<span class="link text-xs link-primary no-underline">Set budget</span>
					{:else}
						<span class="tabular {over ? 'font-semibold text-error' : 'text-base-content/80'}">
							{formatMoney(row.spentCents)}
							<span class="text-base-content/50"> / {formatMoney(row.budgetCents)}</span>
						</span>
					{/if}
				</span>
				{#if row.budgetCents !== null}
					<BudgetBar
						spent={row.spentCents}
						budget={row.budgetCents}
						color={row.color}
						label={row.name}
					/>
					<span class="text-xs {over ? 'font-medium text-error' : 'text-base-content/60'}">
						{#if over}
							Over by {formatMoney(row.spentCents - row.budgetCents)}
						{:else}
							{formatMoney(row.budgetCents - row.spentCents)} left
							{#if row.expensesCents > 0 || row.recurringCents > 0}
								· {formatMoney(row.recurringCents)} recurring + {formatMoney(row.expensesCents)} expenses
							{/if}
						{/if}
					</span>
				{/if}
			</button>
		{/if}
	</li>
{/snippet}

<div class="flex flex-col gap-2">
	{#if budgeted.length === 0}
		<p class="text-sm text-base-content/60">
			No budgets yet. Tap a category below to set a monthly amount; recurring bills and one-off
			expenses count against it.
		</p>
	{:else}
		<ul class="divide-y divide-base-300/60">
			{#each budgeted as row (row.categoryId)}
				{@render rowView(row)}
			{/each}
		</ul>
		<div class="flex items-baseline justify-between border-t border-base-300/60 pt-3 text-sm">
			<span class="font-semibold">Total</span>
			<span
				class="tabular {totalSpent > totalBudget ? 'font-semibold text-error' : ''}"
				data-testid="budget-total"
			>
				{formatMoney(totalSpent)}
				<span class="text-base-content/50">/ {formatMoney(totalBudget)}</span>
			</span>
		</div>
	{/if}

	{#if unbudgeted.length}
		<button
			type="button"
			class="btn min-h-11 justify-between rounded-xl btn-ghost btn-sm"
			aria-expanded={showUnbudgeted}
			onclick={() => (showUnbudgeted = !showUnbudgeted)}
		>
			<span>Unbudgeted ({unbudgeted.length})</span>
			<Icon name={showUnbudgeted ? 'chevron-up' : 'chevron-down'} size={18} />
		</button>
		{#if showUnbudgeted}
			<ul class="divide-y divide-base-300/60" data-testid="unbudgeted">
				{#each unbudgeted as row (row.categoryId)}
					{@render rowView(row)}
				{/each}
			</ul>
		{/if}
	{/if}
</div>
