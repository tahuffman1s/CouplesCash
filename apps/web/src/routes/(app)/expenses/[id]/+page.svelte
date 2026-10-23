<script lang="ts">
	import { enhance } from '$app/forms';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import ExpenseForm from '#lib/components/ExpenseForm.svelte';
	import { formatMoney } from '#lib/utils/money.ts';
	import { formatWeekdayDate } from '#lib/utils/dates.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data, form }: PageProps = $props();
	let submitting = $state(false);
	let confirmDelete = $state(false);

	const e = $derived(data.expense);
	const category = $derived(data.categories.find((c) => c.id === e.categoryId));
	const account = $derived(data.accounts.find((a) => a.id === e.accountId));

	$effect(() => {
		if (form?.saved) toasts.show('Expense saved', 'success');
	});
</script>

<svelte:head><title>{e.name} · CouplesCash</title></svelte:head>

<AppBar title={e.name} subtitle={formatWeekdayDate(e.spentOn)} back="/bills?tab=expenses" />

<main class="flex flex-col gap-4 px-4 pt-2">
	<section class="card border border-base-300/60 bg-base-100 shadow-sm">
		<div class="card-body flex-row items-center gap-3 p-5">
			<span
				class="flex h-12 w-12 items-center justify-center rounded-2xl text-white"
				style:background={category?.color ?? 'var(--color-neutral)'}
			>
				<Icon name={category?.icon ?? 'shopping-bag'} size={24} />
			</span>
			<div class="min-w-0 flex-1">
				<p class="text-3xl font-bold tracking-tight tabular" data-testid="expense-amount">
					{formatMoney(e.amountCents)}
				</p>
				<p class="truncate text-xs text-base-content/60">
					{category?.name ?? 'Uncategorised'}
					{#if account}
						· {e.deducted ? 'Deducted from' : 'Paid from'} {account.name}
					{/if}
				</p>
			</div>
		</div>
	</section>

	<form
		method="POST"
		action="?/update"
		class="card border border-base-300/60 bg-base-100 shadow-sm"
		use:enhance={() => {
			submitting = true;
			return async ({ update }) => {
				await update({ reset: false });
				submitting = false;
			};
		}}
	>
		<div class="card-body gap-4 p-5">
			<h2 class="font-semibold">Edit</h2>
			<ExpenseForm
				expense={e}
				categories={data.categories}
				accounts={data.accounts}
				values={form?.values}
				error={form?.error}
			/>
			<button class="btn min-h-12 rounded-full btn-primary" disabled={submitting}>
				{#if submitting}<span class="loading loading-sm loading-spinner"></span>{/if}
				Save changes
			</button>
		</div>
	</form>

	<form method="POST" action="?/delete" use:enhance class="card border border-error/30 bg-base-100">
		<div class="card-body gap-2 p-5">
			<h2 class="font-semibold">Delete expense</h2>
			<p class="text-sm text-base-content/65">
				{e.deducted
					? 'The amount is added back to the account balance.'
					: 'Removes it from budgets and the month report.'}
			</p>
			{#if confirmDelete}
				<div class="flex gap-2">
					<button class="btn min-h-11 flex-1 rounded-full btn-error">Yes, delete</button>
					<button
						type="button"
						class="btn min-h-11 flex-1 rounded-full btn-ghost"
						onclick={() => (confirmDelete = false)}>Cancel</button
					>
				</div>
			{:else}
				<button
					type="button"
					class="btn min-h-11 rounded-full btn-outline btn-error"
					onclick={() => (confirmDelete = true)}
				>
					<Icon name="trash" size={18} /> Delete
				</button>
			{/if}
		</div>
	</form>
</main>
