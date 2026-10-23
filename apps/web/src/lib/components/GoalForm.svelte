<script lang="ts">
	import { untrack } from 'svelte';
	import type { Account, Goal } from '#lib/types.ts';
	import { GOAL_ICONS, SWATCHES } from '#lib/utils/labels.ts';
	import { centsToInput, currencySymbol } from '#lib/utils/money.ts';
	import Icon from './Icon.svelte';

	let {
		goal,
		accounts,
		values,
		error
	}: {
		goal?: Goal | null;
		/** savings / investment / cash accounts a goal can be linked to */
		accounts: Account[];
		values?: Partial<Record<string, string>>;
		error?: string;
	} = $props();

	const initial = untrack(() => ({
		color: values?.color ?? goal?.color ?? SWATCHES[2],
		icon: values?.icon ?? goal?.icon ?? 'piggy-bank'
	}));
	let color = $state(initial.color);
	let icon = $state(initial.icon);
</script>

{#if error}
	<div class="alert text-sm alert-error" role="alert">{error}</div>
{/if}

<fieldset class="fieldset gap-3">
	<label class="form-control">
		<span class="label-text mb-1 text-sm font-medium">Goal name</span>
		<input
			name="name"
			type="text"
			required
			maxlength="60"
			class="input-bordered input min-h-12 w-full"
			placeholder="Emergency fund"
			defaultValue={values?.name ?? goal?.name ?? ''}
		/>
	</label>

	<div class="grid grid-cols-2 gap-3">
		<label class="form-control">
			<span class="label-text mb-1 text-sm font-medium">Target amount</span>
			<div class="input-bordered input flex min-h-12 w-full items-center gap-2">
				<span class="text-base-content/50">{currencySymbol()}</span>
				<input
					name="target"
					type="text"
					inputmode="decimal"
					required
					class="grow tabular"
					placeholder="5000"
					defaultValue={values?.target ?? (goal ? centsToInput(goal.targetCents) : '')}
				/>
			</div>
		</label>
		<label class="form-control">
			<span class="label-text mb-1 text-sm font-medium"
				>Target date <span class="text-base-content/50">(optional)</span></span
			>
			<input
				name="targetDate"
				type="date"
				class="input-bordered input min-h-12 w-full"
				defaultValue={values?.targetDate ?? goal?.targetDate ?? ''}
			/>
		</label>
	</div>

	<label class="form-control">
		<span class="label-text mb-1 text-sm font-medium">Where is the money?</span>
		<select
			name="accountId"
			class="select-bordered select min-h-12 w-full"
			defaultValue={values?.accountId ?? goal?.accountId ?? ''}
		>
			<option value="">Track manually (contribute by hand)</option>
			{#each accounts as a (a.id)}
				<option value={a.id}>{a.name} · {a.institution}</option>
			{/each}
		</select>
		<span class="label-text-alt mt-1 text-base-content/60">
			Linked goals follow the account balance; manual goals grow when you tap Contribute.
		</span>
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
			{#each GOAL_ICONS as i (i)}
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
</fieldset>
