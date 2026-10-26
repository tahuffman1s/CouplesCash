<script lang="ts">
	import { untrack } from 'svelte';
	import type { Account, Category, Frequency, Kind, RecurringItem } from '#lib/types.ts';
	import { ALL_KINDS, FREQUENCIES } from '#lib/types.ts';
	import { FREQUENCY_LABEL, KIND_LABEL, monthlyEquivalent } from '#lib/utils/labels.ts';
	import { centsToInput, currencySymbol, formatMoney, parseMoney } from '#lib/utils/money.ts';
	import { today } from '#lib/utils/dates.ts';
	import BankNamesInput from './BankNamesInput.svelte';

	let {
		item,
		categories,
		accounts,
		values,
		error
	}: {
		item?: RecurringItem | null;
		categories: Category[];
		accounts: Account[];
		values?: Partial<Record<string, string>>;
		error?: string;
	} = $props();

	const initial = untrack(() => ({
		kind: (values?.kind as Kind | undefined) ?? item?.kind ?? 'bill',
		frequency: (values?.frequency as Frequency | undefined) ?? item?.frequency ?? 'monthly',
		amount: values?.amount ?? (item ? centsToInput(item.amountCents) : '')
	}));
	let kind = $state<Kind>(initial.kind);
	let frequency = $state<Frequency>(initial.frequency);
	let amount = $state(initial.amount);
	const preview = $derived.by(() => {
		const cents = parseMoney(amount);
		if (cents === null || cents <= 0 || frequency === 'monthly') return null;
		return monthlyEquivalent(cents, frequency);
	});
	const incomeCategory = $derived(categories.find((c) => c.name.toLowerCase() === 'income'));
	const defaultCategory = $derived(
		values?.categoryId ?? item?.categoryId ?? (kind === 'income' ? (incomeCategory?.id ?? '') : '')
	);
</script>

{#if error}
	<div class="alert text-sm alert-error" role="alert">{error}</div>
{/if}

<fieldset class="fieldset gap-3">
	<div class="form-control">
		<span class="label-text mb-1 text-sm font-medium">What is it?</span>
		<div class="grid w-full grid-cols-4 gap-1" role="radiogroup" aria-label="Kind">
			{#each ALL_KINDS as k (k)}
				<label
					class="btn min-h-11 rounded-xl px-1 text-xs sm:text-sm {kind === k
						? 'btn-primary'
						: 'border border-base-300 btn-ghost'}"
				>
					<input type="radio" name="kind" value={k} bind:group={kind} class="sr-only" />
					{KIND_LABEL[k]}
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
			placeholder={kind === 'income'
				? 'Paycheck'
				: kind === 'subscription'
					? 'Netflix'
					: kind === 'transfer'
						? 'Fun money · Travis'
						: 'Rent'}
			defaultValue={values?.name ?? item?.name ?? ''}
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
					bind:value={amount}
				/>
			</div>
		</label>
		<label class="form-control">
			<span class="label-text mb-1 text-sm font-medium">Repeats</span>
			<select
				name="frequency"
				class="select-bordered select min-h-12 w-full"
				bind:value={frequency}
			>
				{#each FREQUENCIES as f (f)}
					<option value={f}>{FREQUENCY_LABEL[f]}</option>
				{/each}
			</select>
		</label>
	</div>
	{#if preview !== null}
		<p class="-mt-1 text-xs text-base-content/60">≈ {formatMoney(preview)} per month</p>
	{/if}

	<div class="grid grid-cols-2 gap-3">
		<label class="form-control">
			<span class="label-text mb-1 text-sm font-medium"
				>{frequency === 'once' ? 'Date' : 'First due date'}</span
			>
			<input
				name="anchorDate"
				type="date"
				required
				class="input-bordered input min-h-12 w-full"
				defaultValue={values?.anchorDate ?? item?.anchorDate ?? today()}
			/>
		</label>
		{#if frequency !== 'once'}
			<label class="form-control">
				<span class="label-text mb-1 text-sm font-medium"
					>Ends <span class="text-base-content/50">(optional)</span></span
				>
				<input
					name="endDate"
					type="date"
					class="input-bordered input min-h-12 w-full"
					defaultValue={values?.endDate ?? item?.endDate ?? ''}
				/>
			</label>
		{/if}
	</div>

	{#if kind === 'transfer'}
		<div class="grid grid-cols-2 gap-3">
			<label class="form-control">
				<span class="label-text mb-1 text-sm font-medium">From</span>
				<select
					name="fromAccountId"
					required
					class="select-bordered select min-h-12 w-full"
					defaultValue={values?.fromAccountId ??
						item?.fromAccountId ??
						accounts.find((a) => a.role === 'bills')?.id ??
						''}
				>
					<option value="">Pick one</option>
					{#each accounts as a (a.id)}
						<option value={a.id}>{a.name}</option>
					{/each}
				</select>
			</label>
			<label class="form-control">
				<span class="label-text mb-1 text-sm font-medium">To</span>
				<select
					name="toAccountId"
					required
					class="select-bordered select min-h-12 w-full"
					defaultValue={values?.toAccountId ?? item?.toAccountId ?? ''}
				>
					<option value="">Pick one</option>
					{#each accounts as a (a.id)}
						<option value={a.id}>{a.name}</option>
					{/each}
				</select>
			</label>
		</div>
		<p class="-mt-1 text-xs text-base-content/60">
			Counts in the weekly math as money out of the bills account, never as spending. It ticks
			itself when the transfer shows up in the bank.
		</p>
	{:else}
		<div class="grid grid-cols-2 gap-3">
			<label class="form-control">
				<span class="label-text mb-1 text-sm font-medium">Category</span>
				<select
					name="categoryId"
					class="select-bordered select min-h-12 w-full"
					defaultValue={defaultCategory}
				>
					<option value="">None</option>
					{#each categories as c (c.id)}
						<option value={c.id}>{c.name}</option>
					{/each}
				</select>
			</label>
			<label class="form-control">
				<span class="label-text mb-1 text-sm font-medium"
					>{kind === 'income' ? 'Paid into' : 'Paid from'}</span
				>
				<select
					name="accountId"
					class="select-bordered select min-h-12 w-full"
					defaultValue={values?.accountId ?? item?.accountId ?? ''}
				>
					<option value="">Any account</option>
					{#each accounts as a (a.id)}
						<option value={a.id}>{a.name}</option>
					{/each}
				</select>
			</label>
		</div>
	{/if}

	<BankNamesInput
		names={values?.bankName !== undefined
			? values.bankName.split('\n').filter(Boolean)
			: (item?.bankNames ?? (item?.payeeHint ? [item.payeeHint] : []))}
		learned={item?.bankNamesLearned ?? []}
		itemId={item?.id ?? null}
		placeholder={kind === 'income' ? 'ACME PAYROLL' : 'NETFLIX.COM'}
	/>

	<label class="label cursor-pointer justify-start gap-3 px-0">
		<input
			type="checkbox"
			name="amountVaries"
			class="toggle toggle-secondary"
			defaultChecked={values ? values.amountVaries === 'on' : (item?.amountVaries ?? false)}
		/>
		<span class="label-text"
			>Varies each time <span class="text-base-content/55">(utilities, card payments)</span></span
		>
	</label>

	{#if kind !== 'income' && kind !== 'transfer'}
		<label class="label cursor-pointer justify-start gap-3 px-0">
			<input
				type="checkbox"
				name="autoPay"
				class="toggle toggle-secondary"
				defaultChecked={values ? values.autoPay === 'on' : (item?.autoPay ?? false)}
			/>
			<span class="label-text">Paid automatically (auto-pay)</span>
		</label>
	{/if}

	<label class="form-control">
		<span class="label-text mb-1 text-sm font-medium"
			>Link <span class="text-base-content/50">(optional)</span></span
		>
		<input
			name="url"
			type="url"
			inputmode="url"
			class="input-bordered input min-h-12 w-full"
			placeholder="https://"
			defaultValue={values?.url ?? item?.url ?? ''}
		/>
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
			defaultValue={values?.notes ?? item?.notes ?? ''}></textarea>
	</label>
</fieldset>
