<script lang="ts">
	import { enhance } from '$app/forms';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import GoalForm from '#lib/components/GoalForm.svelte';
	import BottomSheet from '#lib/components/BottomSheet.svelte';
	import ProgressRing from '#lib/charts/ProgressRing.svelte';
	import GoalSpark from '#lib/charts/GoalSpark.svelte';
	import { currencySymbol, formatMoney } from '#lib/utils/money.ts';
	import { formatDate } from '#lib/utils/dates.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data, form }: PageProps = $props();

	const g = $derived(data.goal);
	const reached = $derived(g.currentCents >= g.targetCents);
	const remaining = $derived(Math.max(0, g.targetCents - g.currentCents));

	let editing = $state(false);
	let submitting = $state(false);
	let confirmDelete = $state(false);
	let contributing = $state(false);
	let direction = $state<'add' | 'withdraw'>('add');

	$effect(() => {
		if (form?.saved) {
			editing = false;
			toasts.show('Goal saved', 'success');
		}
		if (form?.contributed) {
			contributing = false;
			toasts.show(
				form.contributed > 0
					? `${formatMoney(form.contributed)} added to ${g.name}`
					: `${formatMoney(-form.contributed)} taken from ${g.name}`,
				'success'
			);
		}
	});
	$effect(() => {
		if (data.created) toasts.show('Goal created', 'success');
	});
</script>

<svelte:head><title>{g.name} · CouplesCash</title></svelte:head>

<AppBar
	title={g.name}
	subtitle={g.accountName ? `Linked to ${g.accountName}` : 'Tracked manually'}
	back="/accounts#goals"
>
	{#snippet action()}
		<button
			type="button"
			class="btn btn-circle min-h-11 min-w-11 btn-ghost btn-sm"
			aria-label={editing ? 'Stop editing' : 'Edit goal'}
			aria-pressed={editing}
			onclick={() => (editing = !editing)}
		>
			<Icon name={editing ? 'x' : 'pencil'} size={20} />
		</button>
	{/snippet}
</AppBar>

<main class="flex flex-col gap-4 px-4 pt-2">
	{#if !editing}
		<section class="card border border-base-300/60 bg-base-100 shadow-sm">
			<div class="card-body gap-4 p-5">
				<div class="flex items-center gap-4">
					<ProgressRing
						percent={g.percent}
						color={g.color}
						size={96}
						thickness={9}
						label="{Math.round(g.percent)}% of {g.name} saved"
					>
						<span class="text-lg font-bold" data-testid="goal-percent"
							>{Math.round(g.percent)}%</span
						>
					</ProgressRing>
					<div class="min-w-0 flex-1">
						<p class="text-xs text-base-content/60">Saved</p>
						<p class="text-3xl font-bold tracking-tight" data-testid="goal-current">
							{formatMoney(g.currentCents, { whole: true })}
						</p>
						<p class="text-sm text-base-content/60 tabular">
							of {formatMoney(g.targetCents, { whole: true })}
							{#if !reached}· {formatMoney(remaining, { whole: true })} to go{/if}
						</p>
					</div>
				</div>

				<dl class="grid grid-cols-2 gap-x-4 gap-y-2 text-sm">
					{#if g.targetDate}
						<dt class="text-base-content/60">Target date</dt>
						<dd class="font-medium">{formatDate(g.targetDate)}</dd>
					{/if}
					{#if g.monthlyNeededCents !== null && !reached}
						<dt class="text-base-content/60">Needed per month</dt>
						<dd class="font-medium tabular">
							{formatMoney(g.monthlyNeededCents, { whole: true })}
						</dd>
					{/if}
					{#if g.monthlyPaceCents !== null}
						<dt class="text-base-content/60">Recent pace</dt>
						<dd class="font-medium tabular {g.monthlyPaceCents < 0 ? 'text-error' : ''}">
							{formatMoney(g.monthlyPaceCents, { signed: true, whole: true })}/mo
						</dd>
					{/if}
					<dt class="text-base-content/60">Projected</dt>
					<dd class="font-medium">
						{#if reached}
							Reached 🎉
						{:else if g.projectedDate}
							{formatDate(g.projectedDate)}
						{:else}
							Not enough history yet
						{/if}
					</dd>
				</dl>

				{#if g.accountId}
					<a href="/accounts/{g.accountId}" class="btn min-h-12 rounded-full btn-secondary">
						<Icon name="refresh-cw" size={18} /> Update the linked account balance
					</a>
				{:else}
					<button
						type="button"
						class="btn min-h-12 rounded-full btn-primary"
						onclick={() => {
							direction = 'add';
							contributing = true;
						}}
					>
						<Icon name="plus" size={18} strokeWidth={2.5} /> Contribute
					</button>
				{/if}
			</div>
		</section>

		<section class="card border border-base-300/60 bg-base-100 shadow-sm" aria-labelledby="hist">
			<div class="card-body gap-3 p-5">
				<h2 id="hist" class="font-semibold">Progress</h2>
				<GoalSpark points={g.history} target={g.targetCents} color={g.color} />
			</div>
		</section>
	{:else}
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
				<GoalForm goal={g} accounts={data.accounts} values={form?.values} error={form?.error} />
				<button class="btn min-h-12 rounded-full btn-primary" disabled={submitting}>
					{#if submitting}<span class="loading loading-sm loading-spinner"></span>{/if}
					Save changes
				</button>
			</div>
		</form>

		<form
			method="POST"
			action="?/delete"
			use:enhance
			class="card border border-error/30 bg-base-100"
		>
			<div class="card-body gap-2 p-5">
				<h2 class="font-semibold">Remove goal</h2>
				<p class="text-sm text-base-content/65">The linked account (if any) is untouched.</p>
				{#if confirmDelete}
					<div class="flex gap-2">
						<button class="btn min-h-11 flex-1 rounded-full btn-error">Yes, remove</button>
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
						<Icon name="trash" size={18} /> Remove goal
					</button>
				{/if}
			</div>
		</form>
	{/if}
</main>

<BottomSheet bind:open={contributing} title="Contribute to {g.name}">
	<form method="POST" action="?/contribute" class="flex flex-col gap-3" use:enhance>
		<div class="join w-full" role="radiogroup" aria-label="Direction">
			<label
				class="btn join-item min-h-11 flex-1 {direction === 'add'
					? 'btn-primary'
					: 'border border-base-300 btn-ghost'}"
			>
				<input type="radio" name="direction" value="add" class="sr-only" bind:group={direction} /> Add
			</label>
			<label
				class="btn join-item min-h-11 flex-1 {direction === 'withdraw'
					? 'btn-primary'
					: 'border border-base-300 btn-ghost'}"
			>
				<input
					type="radio"
					name="direction"
					value="withdraw"
					class="sr-only"
					bind:group={direction}
				/> Take out
			</label>
		</div>
		<label class="form-control">
			<span class="label-text mb-1 text-sm font-medium">Amount</span>
			<div class="input-bordered input flex min-h-12 w-full items-center gap-2">
				<span class="text-base-content/50">{currencySymbol()}</span>
				<input
					name="amount"
					type="text"
					inputmode="decimal"
					required
					class="grow tabular"
					placeholder="50"
					data-testid="contribute-amount"
				/>
			</div>
		</label>
		{#if form?.contributeError}<p class="text-sm text-error" role="alert">
				{form.contributeError}
			</p>{/if}
		<button class="btn min-h-12 rounded-full btn-primary">
			{direction === 'add' ? 'Add to goal' : 'Take out'}
		</button>
	</form>
</BottomSheet>
