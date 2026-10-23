<script lang="ts">
	import type { PayPeriodView } from '#lib/types.ts';
	import { formatMoney } from '#lib/utils/money.ts';
	import { formatWeekdayDate, today } from '#lib/utils/dates.ts';
	import { periodProgress } from '#lib/utils/payperiod.ts';
	import Icon from './Icon.svelte';

	let {
		view,
		date = today(),
		compact = true
	}: {
		/** null → the household has not configured a pay period yet */
		view: PayPeriodView | null;
		/** the date the view was computed for */
		date?: string;
		/** compact: dashboard card with a link; full: header of /payperiod */
		compact?: boolean;
	} = $props();

	const elapsed = $derived(view ? Math.round(periodProgress(view, date) * 100) : 0);
</script>

{#if !view}
	<a
		href="/settings/household#payperiod"
		class="card border border-dashed border-base-300 bg-base-100/60 text-sm shadow-none"
		data-testid="payday-setup"
	>
		<div class="card-body flex-row items-center gap-3 px-4 py-3">
			<Icon name="calendar-clock" size={20} class="text-secondary" />
			<span class="flex-1">Set your payday to see what's safe to spend until then.</span>
			<Icon name="chevron-right" size={18} class="text-base-content/40" />
		</div>
	</a>
{:else}
	<section
		class="card border border-base-300/60 bg-base-100 shadow-sm"
		aria-labelledby="payday"
		data-testid="payday-card"
	>
		<div class="card-body gap-3 p-5">
			<div class="flex items-start justify-between gap-3">
				<div>
					<h2 id="payday" class="text-xs font-medium tracking-wide text-base-content/60 uppercase">
						Until payday
					</h2>
					<p class="font-semibold">
						{formatWeekdayDate(view.nextPayday)}
						<span class="font-normal text-base-content/60">
							· {view.daysLeft === 0
								? 'today'
								: view.daysLeft === 1
									? 'tomorrow'
									: `${view.daysLeft} days left`}
						</span>
					</p>
				</div>
				{#if compact}
					<a href="/payperiod" class="link text-sm link-primary no-underline">Details</a>
				{/if}
			</div>

			<div>
				<p class="text-xs text-base-content/60">Spendable</p>
				<p
					class="text-4xl leading-none font-bold tracking-tight {view.spendableCents < 0
						? 'text-error'
						: ''}"
					data-testid="spendable"
				>
					{formatMoney(view.spendableCents, { whole: true })}
				</p>
				<p class="mt-1 text-xs text-base-content/60">
					Checking + cash, minus what's still due this period
				</p>
			</div>

			<div class="grid grid-cols-4 gap-2 text-center text-xs">
				<div class="rounded-xl bg-base-200 px-1 py-2">
					<p class="text-base-content/60">Income</p>
					<p class="font-semibold text-success tabular">
						{formatMoney(view.expectedIncomeCents, { whole: true })}
					</p>
				</div>
				<div class="rounded-xl bg-base-200 px-1 py-2">
					<p class="text-base-content/60">Due</p>
					<p class="font-semibold tabular">{formatMoney(view.dueCents, { whole: true })}</p>
				</div>
				<div class="rounded-xl bg-base-200 px-1 py-2">
					<p class="text-base-content/60">Paid</p>
					<p class="font-semibold tabular">{formatMoney(view.paidCents, { whole: true })}</p>
				</div>
				<div class="rounded-xl bg-base-200 px-1 py-2">
					<p class="text-base-content/60">Left</p>
					<p class="font-semibold tabular">{formatMoney(view.remainingCents, { whole: true })}</p>
				</div>
			</div>

			<div>
				<progress
					class="progress h-2 w-full progress-secondary"
					value={elapsed}
					max="100"
					aria-label="{elapsed}% of the pay period elapsed"
				></progress>
				<p class="mt-1 flex justify-between text-xs text-base-content/60">
					<span
						>Period {view.start.slice(5).replace('-', '/')} – {view.end
							.slice(5)
							.replace('-', '/')}</span
					>
					<span>{elapsed}% elapsed</span>
				</p>
			</div>
		</div>
	</section>
{/if}
