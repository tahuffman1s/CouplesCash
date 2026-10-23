<script lang="ts">
	import { untrack } from 'svelte';
	import type { Account, AccountType } from '#lib/types.ts';
	import { ACCOUNT_TYPES } from '#lib/types.ts';
	import { ACCOUNT_ICONS, ACCOUNT_TYPE_LABEL, SWATCHES } from '#lib/utils/labels.ts';
	import { centsToInput } from '#lib/utils/money.ts';
	import Icon from './Icon.svelte';

	let {
		account,
		values,
		error
	}: {
		account?: Account | null;
		/** values echoed back from a failed submit */
		values?: Partial<Record<string, string>>;
		error?: string;
	} = $props();

	// Seed editable local state from props once; the inputs own the value from then on.
	const initial = untrack(() => ({
		type: (values?.type as AccountType | undefined) ?? account?.type ?? 'checking',
		color: values?.color ?? account?.color ?? SWATCHES[0],
		icon: values?.icon ?? account?.icon ?? 'landmark'
	}));
	let type = $state<AccountType>(initial.type);
	let color = $state(initial.color);
	let icon = $state(initial.icon);
	const liability = $derived(type === 'credit' || type === 'loan');
</script>

{#if error}
	<div class="alert text-sm alert-error" role="alert">{error}</div>
{/if}

<fieldset class="fieldset gap-3">
	<label class="form-control">
		<span class="label-text mb-1 text-sm font-medium">Account name</span>
		<input
			name="name"
			type="text"
			required
			maxlength="60"
			class="input-bordered input min-h-12 w-full"
			placeholder="Everyday checking"
			defaultValue={values?.name ?? account?.name ?? ''}
		/>
	</label>

	<label class="form-control">
		<span class="label-text mb-1 text-sm font-medium">Bank or institution</span>
		<input
			name="institution"
			type="text"
			required
			maxlength="60"
			class="input-bordered input min-h-12 w-full"
			placeholder="Chase"
			defaultValue={values?.institution ?? account?.institution ?? ''}
		/>
	</label>

	<div class="form-control">
		<span class="label-text mb-1 text-sm font-medium">Type</span>
		<div class="grid grid-cols-3 gap-2" role="radiogroup" aria-label="Account type">
			{#each ACCOUNT_TYPES as t (t)}
				<label
					class="btn min-h-11 justify-start gap-2 rounded-xl px-3 text-left text-sm font-medium {type ===
					t
						? 'btn-primary'
						: 'border border-base-300 btn-ghost'}"
				>
					<input type="radio" name="type" value={t} bind:group={type} class="sr-only" />
					{ACCOUNT_TYPE_LABEL[t]}
				</label>
			{/each}
		</div>
	</div>

	<label class="form-control">
		<span class="label-text mb-1 text-sm font-medium">
			{liability ? 'Amount owed' : 'Current balance'}
		</span>
		<div class="input-bordered input flex min-h-12 w-full items-center gap-2">
			<span class="text-base-content/50">$</span>
			<input
				name="balance"
				type="text"
				inputmode="decimal"
				required
				class="grow tabular"
				placeholder="0.00"
				defaultValue={values?.balance ?? (account ? centsToInput(account.balanceCents) : '')}
			/>
		</div>
		{#if liability}
			<span class="label-text-alt mt-1 text-base-content/60">
				Enter what you owe as a positive number; it counts against your net worth.
			</span>
		{/if}
	</label>

	<div class="form-control">
		<span class="label-text mb-1 text-sm font-medium">Colour</span>
		<div class="flex flex-wrap gap-2" role="radiogroup" aria-label="Colour">
			{#each SWATCHES as c (c)}
				<label
					class="h-11 w-11 cursor-pointer rounded-full border-4 {color === c
						? 'border-base-content/70'
						: 'border-transparent'}"
					style:background={c}
				>
					<input type="radio" name="color" value={c} bind:group={color} class="sr-only" />
					<span class="sr-only">{c}</span>
				</label>
			{/each}
		</div>
	</div>

	<div class="form-control">
		<span class="label-text mb-1 text-sm font-medium">Icon</span>
		<div class="flex flex-wrap gap-2" role="radiogroup" aria-label="Icon">
			{#each ACCOUNT_ICONS as i (i)}
				<label
					class="flex h-11 w-11 cursor-pointer items-center justify-center rounded-xl border {icon ===
					i
						? 'border-primary bg-primary/10 text-primary'
						: 'border-base-300'}"
				>
					<input type="radio" name="icon" value={i} bind:group={icon} class="sr-only" />
					<Icon name={i} size={20} label={i} />
				</label>
			{/each}
		</div>
	</div>

	<label class="form-control">
		<span class="label-text mb-1 text-sm font-medium"
			>Note <span class="text-base-content/50">(optional)</span></span
		>
		<textarea
			name="note"
			rows="2"
			maxlength="200"
			class="textarea-bordered textarea w-full"
			placeholder="Joint account for bills"
			defaultValue={values?.note ?? account?.note ?? ''}></textarea>
	</label>
</fieldset>
