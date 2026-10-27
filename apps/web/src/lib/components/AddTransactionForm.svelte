<script lang="ts">
	import { enhance } from '$app/forms';
	import type { Account } from '#lib/types.ts';
	import type { QuickAddForm } from './QuickAddSheet.svelte';
	import { currencySymbol } from '#lib/utils/money.ts';
	import { today } from '#lib/utils/dates.ts';

	/**
	 * The "+" sheet's Transaction tab: a charge (or money in) the bank hasn't shown yet. Posts
	 * `?/addTransaction`; it counts in This week right away and the bank's own copy replaces it
	 * when it shows up. When the bank already shows something like it, "Add anyway" resends with
	 * `force=1`.
	 */
	let {
		accounts,
		accountId,
		form = null
	}: { accounts: Account[]; accountId: string | null; form?: QuickAddForm | null } = $props();

	const values = $derived(form?.addValues);
	let direction = $state<'out' | 'in'>('out');
	let busy = $state(false);
	let amountInput = $state<HTMLInputElement | null>(null);
	$effect(() => {
		if (values?.direction) direction = values.direction;
	});
	$effect(() => {
		queueMicrotask(() => amountInput?.focus());
	});
	const selected = $derived(values?.accountId || accountId || accounts[0]?.id || '');
	const segment = 'btn join-item min-h-11 flex-1 border-base-300 font-medium shadow-none';
</script>

<form
	method="POST"
	action="?/addTransaction"
	class="flex flex-col gap-3"
	data-testid="add-tx-form"
	use:enhance={() => {
		busy = true;
		return async ({ update }) => {
			await update({ reset: false });
			busy = false;
		};
	}}
>
	<div class="join w-full" role="radiogroup" aria-label="Money out or in">
		<label class="{segment} {direction === 'out' ? 'btn-neutral' : 'bg-base-100'}">
			<input
				type="radio"
				name="direction"
				value="out"
				class="sr-only"
				bind:group={direction}
				data-testid="add-tx-out"
			/>
			Money out
		</label>
		<label class="{segment} {direction === 'in' ? 'btn-neutral' : 'bg-base-100'}">
			<input
				type="radio"
				name="direction"
				value="in"
				class="sr-only"
				bind:group={direction}
				data-testid="add-tx-in"
			/>
			Money in
		</label>
	</div>

	<label class="form-control">
		<span class="label-text mb-1 text-sm font-medium">Amount</span>
		<span class="input-bordered input flex min-h-12 w-full items-center gap-2">
			<span class="text-base-content/50">{currencySymbol()}</span>
			<input
				bind:this={amountInput}
				name="amount"
				type="text"
				inputmode="decimal"
				autocomplete="off"
				class="grow text-base tabular"
				placeholder="0.00"
				defaultValue={values?.amount ?? ''}
				data-testid="add-tx-amount"
			/>
		</span>
	</label>

	<label class="form-control">
		<span class="label-text mb-1 text-sm font-medium">What was it?</span>
		<input
			name="description"
			type="text"
			maxlength="120"
			autocomplete="off"
			class="input-bordered input min-h-12 w-full text-base"
			placeholder="Walmart"
			defaultValue={values?.description ?? ''}
			data-testid="add-tx-description"
		/>
	</label>

	<div class="grid grid-cols-2 gap-2">
		<label class="form-control min-w-0">
			<span class="label-text mb-1 text-sm font-medium">Account</span>
			<select
				name="accountId"
				class="select-bordered select min-h-11 w-full"
				data-testid="add-tx-account"
			>
				{#each accounts as a (a.id)}
					<option value={a.id} selected={a.id === selected}>{a.name}</option>
				{/each}
			</select>
		</label>
		<label class="form-control min-w-0">
			<span class="label-text mb-1 text-sm font-medium">Date</span>
			<input
				name="postedOn"
				type="date"
				class="input-bordered input min-h-11 w-full"
				defaultValue={values?.postedOn ?? today()}
				data-testid="add-tx-date"
			/>
		</label>
	</div>

	{#if form?.addError}
		<div
			class="alert text-sm {form.addDuplicate ? 'alert-warning' : 'alert-error'}"
			role="alert"
			data-testid="add-tx-error"
		>
			{form.addError}
		</div>
	{/if}

	<p class="text-xs text-base-content/60">
		For something your bank hasn't shown yet. It counts in This week right away, and when the bank's
		copy shows up it takes its place, so nothing counts twice.
	</p>

	{#if form?.addDuplicate}
		<button
			name="force"
			value="1"
			class="btn min-h-12 rounded-full btn-outline"
			disabled={busy}
			data-testid="add-tx-force"
		>
			{#if busy}<span class="loading loading-sm loading-spinner"></span>{/if}
			Add anyway
		</button>
	{:else}
		<button
			class="btn min-h-12 rounded-full btn-primary"
			disabled={busy}
			data-testid="add-tx-submit"
		>
			{#if busy}<span class="loading loading-sm loading-spinner"></span>{/if}
			Add transaction
		</button>
	{/if}
</form>
