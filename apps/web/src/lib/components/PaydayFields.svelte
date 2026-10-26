<script lang="ts">
	import { untrack } from 'svelte';
	import { currencySymbol, centsToInput, signedMoney } from '#lib/utils/money.ts';
	import { WEEKDAYS_SHORT, weekdayName } from '#lib/utils/plan.ts';

	/**
	 * v7 payday: the weekday the pay week starts on (default Friday) and the paycheck amount
	 * (prefilled from the income item, or from a deposit found in the bank). Inside the caller's form.
	 */
	let {
		weekday = 5,
		paycheckCents = null,
		found = null,
		incomeCategoryId = null
	}: {
		weekday?: number;
		paycheckCents?: number | null;
		/** a paycheck spotted in the bank feed ("ACME CORP PAYROLL") */
		found?: { bankName: string | null; amountCents: number | null } | null;
		incomeCategoryId?: string | null;
	} = $props();

	let day = $state(untrack(() => weekday));
	const end = $derived(weekdayName(day + 6));
</script>

<div class="flex flex-col gap-4">
	<div class="form-control">
		<span class="label-text mb-1 text-sm font-medium">Payday</span>
		<div class="grid grid-cols-7 gap-1" role="radiogroup" aria-label="Payday">
			{#each WEEKDAYS_SHORT as label, i (label)}
				<label
					class="btn h-auto min-h-11 rounded-xl px-0 text-xs font-medium {day === i
						? 'btn-primary'
						: 'border border-base-300 btn-ghost'}"
				>
					<input
						type="radio"
						name="paydayWeekday"
						value={i}
						class="sr-only"
						checked={day === i}
						onchange={() => (day = i)}
						aria-label={weekdayName(i)}
					/>
					{label}
				</label>
			{/each}
		</div>
		<span class="label-text-alt mt-1 text-base-content/60" data-testid="payweek-hint"
			>Your week runs {weekdayName(day)} → {end}.</span
		>
	</div>
	<label class="form-control">
		<span class="label-text mb-1 text-sm font-medium">Paycheck</span>
		<div class="input-bordered input flex min-h-12 w-full items-center gap-2">
			<span class="text-base-content/50">{currencySymbol()}</span>
			<input
				name="paycheck"
				type="text"
				inputmode="decimal"
				class="grow tabular"
				placeholder="2450.00"
				aria-label="Paycheck amount"
				defaultValue={paycheckCents ? centsToInput(paycheckCents) : ''}
			/>
		</div>
		{#if found?.bankName && found.amountCents}
			<span class="label-text-alt mt-1 text-base-content/60" data-testid="paycheck-found"
				>Found in your bank: {found.bankName} {signedMoney(found.amountCents)}</span
			>
		{:else}
			<span class="label-text-alt mt-1 text-base-content/60"
				>What lands in the bills account each payday. Leave blank to skip.</span
			>
		{/if}
	</label>
	<input type="hidden" name="paycheckBankName" value={found?.bankName ?? ''} />
	<input type="hidden" name="incomeCategoryId" value={incomeCategoryId ?? ''} />
</div>
