<script lang="ts">
	import { untrack } from 'svelte';
	import type { Account, Category, Expense } from '#lib/types.ts';
	import { centsToInput, currencySymbol } from '#lib/utils/money.ts';
	import { today } from '#lib/utils/dates.ts';

	let {
		expense,
		categories,
		accounts,
		defaultAccountId = null,
		values,
		error
	}: {
		expense?: Expense | null;
		categories: Category[];
		accounts: Account[];
		/** household default account, preselected for new expenses */
		defaultAccountId?: string | null;
		values?: Partial<Record<string, string>>;
		error?: string;
	} = $props();

	const initial = untrack(() => ({
		accountId: values?.accountId ?? expense?.accountId ?? defaultAccountId ?? '',
		deduct: values ? values.deduct === 'on' : (expense?.deducted ?? true)
	}));
	let accountId = $state(initial.accountId);
	let deduct = $state(initial.deduct);
</script>

{#if error}
	<div class="alert text-sm alert-error" role="alert">{error}</div>
{/if}

<fieldset class="fieldset gap-3">
	<label class="form-control">
		<span class="label-text mb-1 text-sm font-medium">What was it?</span>
		<input
			name="name"
			type="text"
			required
			maxlength="80"
			class="input-bordered input min-h-12 w-full"
			placeholder="Groceries at Trader Joe's"
			defaultValue={values?.name ?? expense?.name ?? ''}
		/>
	</label>

	<div class="grid grid-cols-2 gap-3">
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
					placeholder="0.00"
					defaultValue={values?.amount ?? (expense ? centsToInput(expense.amountCents) : '')}
				/>
			</div>
		</label>
		<label class="form-control">
			<span class="label-text mb-1 text-sm font-medium">Date</span>
			<input
				name="spentOn"
				type="date"
				required
				class="input-bordered input min-h-12 w-full"
				defaultValue={values?.spentOn ?? expense?.spentOn ?? today()}
			/>
		</label>
	</div>

	<div class="grid grid-cols-2 gap-3">
		<label class="form-control">
			<span class="label-text mb-1 text-sm font-medium">Category</span>
			<select
				name="categoryId"
				class="select-bordered select min-h-12 w-full"
				defaultValue={values?.categoryId ?? expense?.categoryId ?? ''}
			>
				<option value="">None</option>
				{#each categories as c (c.id)}
					<option value={c.id}>{c.name}</option>
				{/each}
			</select>
		</label>
		<label class="form-control">
			<span class="label-text mb-1 text-sm font-medium">Account</span>
			<select
				name="accountId"
				class="select-bordered select min-h-12 w-full"
				bind:value={accountId}
			>
				<option value="">No account</option>
				{#each accounts as a (a.id)}
					<option value={a.id}>{a.name}</option>
				{/each}
			</select>
		</label>
	</div>

	{#if accountId}
		<label class="label cursor-pointer justify-start gap-3 px-0">
			<input type="checkbox" name="deduct" class="toggle toggle-secondary" bind:checked={deduct} />
			<span class="label-text">Deduct from account balance</span>
		</label>
	{/if}

	<label class="form-control">
		<span class="label-text mb-1 text-sm font-medium"
			>Note <span class="text-base-content/50">(optional)</span></span
		>
		<textarea
			name="note"
			rows="2"
			maxlength="300"
			class="textarea-bordered textarea w-full"
			defaultValue={values?.note ?? expense?.note ?? ''}></textarea>
	</label>
</fieldset>
