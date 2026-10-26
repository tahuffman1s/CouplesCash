<script lang="ts">
	import type { FunRow } from '#lib/utils/setup.ts';
	import { centsToInput, currencySymbol } from '#lib/utils/money.ts';

	/**
	 * v7 fun money: $20 a week and $150 a month each (prefilled, editable). Saved as `transfer`
	 * items from the bills account to each fun account. Inside the caller's form.
	 */
	let { rows, paydayName }: { rows: FunRow[]; paydayName: string } = $props();

	const amount = (cents: number) => (cents ? centsToInput(cents).replace(/\.00$/, '') : '');
</script>

{#if rows.length === 0}
	<p class="rounded-xl bg-base-200/70 p-3 text-sm text-base-content/70" data-testid="fun-none">
		No fun-money account yet. Mark each person's account as <strong>Fun money</strong> under
		<a href="/setup?step=roles" class="link link-primary">Which account is which</a>.
	</p>
{:else}
	<ul class="flex flex-col gap-3" data-testid="fun-fields">
		{#each rows as r (r.accountId)}
			<li class="rounded-2xl border border-base-300/70 p-3" data-testid="fun-row">
				<p class="mb-2 text-sm font-semibold">{r.label}</p>
				<div class="grid grid-cols-2 gap-2">
					<label class="form-control">
						<span class="label-text mb-1 text-xs text-base-content/70">Every {paydayName}</span>
						<div class="input-bordered input flex min-h-11 w-full items-center gap-1">
							<span class="text-base-content/50">{currencySymbol()}</span>
							<input
								name="weekly_{r.accountId}"
								type="text"
								inputmode="decimal"
								class="w-full tabular"
								aria-label="Weekly fun money for {r.memberName}"
								defaultValue={amount(r.weeklyCents)}
							/>
						</div>
					</label>
					<label class="form-control">
						<span class="label-text mb-1 text-xs text-base-content/70">Start of the month</span>
						<div class="input-bordered input flex min-h-11 w-full items-center gap-1">
							<span class="text-base-content/50">{currencySymbol()}</span>
							<input
								name="monthly_{r.accountId}"
								type="text"
								inputmode="decimal"
								class="w-full tabular"
								aria-label="Monthly fun money for {r.memberName}"
								defaultValue={amount(r.monthlyCents)}
							/>
						</div>
					</label>
				</div>
			</li>
		{/each}
	</ul>
	<p class="text-xs text-base-content/60">
		They come out of the bills account and count in the weekly math. Blank or 0 = none.
	</p>
{/if}
