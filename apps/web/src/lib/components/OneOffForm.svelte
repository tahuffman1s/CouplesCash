<script lang="ts">
	import { untrack } from 'svelte';
	import type { Account } from '#lib/types.ts';
	import { currencySymbol } from '#lib/utils/money.ts';
	import { today } from '#lib/utils/dates.ts';

	/**
	 * "Plan a one-off": the fields of the inline form shared by the dashboard quick-add sheet and
	 * the week view. The parent wraps it in a <form method="POST" action="?/planOneOff">.
	 */
	let {
		accounts,
		defaultAccountId = null,
		defaultDate = today(),
		values,
		error
	}: {
		accounts: Account[];
		defaultAccountId?: string | null;
		defaultDate?: string;
		values?: Partial<Record<string, string>>;
		error?: string;
	} = $props();

	let kind = $state<'expense' | 'income'>(
		untrack(() => (values?.kind === 'income' ? 'income' : 'expense'))
	);
</script>

{#if error}
	<div class="alert text-sm alert-error" role="alert">{error}</div>
{/if}

<div class="form-control">
	<span class="label-text mb-1 text-sm font-medium">What kind?</span>
	<div class="join w-full" role="radiogroup" aria-label="One-off kind">
		{#each [{ v: 'expense', l: 'Expense' }, { v: 'income', l: 'Income' }] as const as o (o.v)}
			<label
				class="btn join-item min-h-11 flex-1 {kind === o.v
					? 'btn-primary'
					: 'border border-base-300 btn-ghost'}"
			>
				<input type="radio" name="kind" value={o.v} bind:group={kind} class="sr-only" />
				{o.l}
			</label>
		{/each}
	</div>
</div>

<label class="form-control">
	<span class="label-text mb-1 text-sm font-medium">Name</span>
	<input
		name="name"
		type="text"
		required
		maxlength="80"
		class="input-bordered input min-h-12 w-full"
		placeholder={kind === 'income' ? 'Tax refund' : 'Oil change'}
		defaultValue={values?.name ?? ''}
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
				defaultValue={values?.amount ?? ''}
			/>
		</div>
	</label>
	<label class="form-control">
		<span class="label-text mb-1 text-sm font-medium">Date</span>
		<input
			name="date"
			type="date"
			required
			class="input-bordered input min-h-12 w-full"
			defaultValue={values?.date ?? defaultDate}
		/>
	</label>
</div>

<label class="form-control">
	<span class="label-text mb-1 text-sm font-medium"
		>{kind === 'income' ? 'Paid into' : 'Paid from'}</span
	>
	<select
		name="accountId"
		class="select-bordered select min-h-12 w-full"
		defaultValue={values?.accountId ?? defaultAccountId ?? ''}
	>
		<option value="">Any account</option>
		{#each accounts as a (a.id)}
			<option value={a.id}>{a.name}</option>
		{/each}
	</select>
</label>

<label class="form-control">
	<span class="label-text mb-1 text-sm font-medium"
		>Bank shows it as <span class="text-base-content/50">(optional)</span></span
	>
	<input
		name="payeeHint"
		type="text"
		maxlength="80"
		autocapitalize="characters"
		class="input-bordered input min-h-12 w-full"
		placeholder="JIFFY LUBE"
		defaultValue={values?.payeeHint ?? ''}
	/>
	<span class="label-text-alt mt-1 text-base-content/60">
		The payee on your statement, so it reconciles itself when the transaction clears.
	</span>
</label>

<label class="form-control">
	<span class="label-text mb-1 text-sm font-medium"
		>Notes <span class="text-base-content/50">(optional)</span></span
	>
	<textarea
		name="notes"
		rows="2"
		maxlength="300"
		class="textarea-bordered textarea w-full"
		defaultValue={values?.notes ?? ''}></textarea>
</label>
