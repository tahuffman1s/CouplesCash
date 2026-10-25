<script lang="ts">
	import { untrack } from 'svelte';
	import { enhance } from '$app/forms';
	import type { PayPeriod } from '#lib/types.ts';
	import { COMMON_CURRENCIES, PAY_PERIODS } from '#lib/types.ts';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import { centsToInput, currencySymbol, formatMoney } from '#lib/utils/money.ts';
	import { formatWeekdayDate, isValidISODate, today, diffDays } from '#lib/utils/dates.ts';
	import { nextPayday } from '#lib/utils/payperiod.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data, form }: PageProps = $props();

	const hh = $derived(data.household);
	const initial = untrack(() => ({
		payPeriod: (form?.values?.payPeriod as PayPeriod | undefined) ?? data.household.payPeriod,
		anchor: form?.values?.payAnchorDate ?? data.household.payAnchorDate ?? '',
		currency: data.household.currency,
		timezone: data.household.timezone
	}));
	let payPeriod = $state<PayPeriod>(initial.payPeriod);
	let anchor = $state(initial.anchor);
	let currencyPick = $state(
		COMMON_CURRENCIES.some((c) => c.code === initial.currency) ? initial.currency : 'other'
	);
	let detectedTz = $state('');

	const preview = $derived.by(() => {
		if (!anchor || !isValidISODate(anchor)) return null;
		const next = nextPayday(payPeriod, anchor, today());
		return { date: next, days: diffDays(today(), next) };
	});

	$effect(() => {
		try {
			detectedTz = Intl.DateTimeFormat().resolvedOptions().timeZone ?? '';
		} catch {
			detectedTz = '';
		}
	});
	$effect(() => {
		if (form?.saved) toasts.show('Household settings saved', 'success');
	});
	const errorFor = (section: string) => (form?.section === section ? form.error : undefined);
</script>

<svelte:head><title>Household settings · CouplesCash</title></svelte:head>

<AppBar title="Household settings" subtitle={hh.name} back="/more" />

<main class="flex flex-col gap-4 px-4 pt-2">
	<!-- General -->
	<section class="card border border-base-300/60 bg-base-100 shadow-sm" aria-labelledby="general">
		<form method="POST" action="?/general" class="card-body gap-3 p-5" use:enhance>
			<h2 id="general" class="font-semibold">General</h2>
			{#if errorFor('general')}
				<div class="alert text-sm alert-error" role="alert">{errorFor('general')}</div>
			{/if}
			<label class="form-control">
				<span class="label-text mb-1 text-sm font-medium">Household name</span>
				<input
					name="name"
					type="text"
					required
					maxlength="40"
					class="input-bordered input min-h-12 w-full"
					defaultValue={form?.values?.name ?? hh.name}
				/>
			</label>

			<div class="grid grid-cols-2 gap-3">
				<label class="form-control">
					<span class="label-text mb-1 text-sm font-medium">Currency</span>
					<select
						name="currency"
						class="select-bordered select min-h-12 w-full"
						bind:value={currencyPick}
					>
						{#each COMMON_CURRENCIES as c (c.code)}
							<option value={c.code}>{c.code} · {c.name}</option>
						{/each}
						<option value="other">Other…</option>
					</select>
				</label>
				{#if currencyPick === 'other'}
					<label class="form-control">
						<span class="label-text mb-1 text-sm font-medium">ISO code</span>
						<input
							name="currencyCustom"
							type="text"
							maxlength="3"
							minlength="3"
							pattern={'[A-Za-z]{3}'}
							autocapitalize="characters"
							class="input-bordered input min-h-12 w-full uppercase"
							placeholder="PLN"
							defaultValue={form?.values?.currencyCustom ??
								(COMMON_CURRENCIES.some((c) => c.code === hh.currency) ? '' : hh.currency)}
						/>
					</label>
				{:else}
					<div class="form-control">
						<span class="label-text mb-1 text-sm font-medium">Preview</span>
						<p class="flex min-h-12 items-center text-lg font-semibold tabular">
							{formatMoney(123456, { currency: currencyPick })}
						</p>
					</div>
				{/if}
			</div>

			<div class="form-control">
				<span class="label-text mb-1 text-sm font-medium">Week starts on</span>
				<div class="join w-full" role="radiogroup" aria-label="Week starts on">
					{#each [{ v: '0', l: 'Sunday' }, { v: '1', l: 'Monday' }] as o (o.v)}
						<label
							class="btn join-item min-h-11 flex-1 {String(
								form?.values?.weekStart ?? hh.weekStart
							) === o.v
								? 'btn-primary'
								: 'border border-base-300 btn-ghost'}"
						>
							<input
								type="radio"
								name="weekStart"
								value={o.v}
								class="sr-only"
								defaultChecked={String(form?.values?.weekStart ?? hh.weekStart) === o.v}
							/>
							{o.l}
						</label>
					{/each}
				</div>
			</div>

			<label class="form-control">
				<span class="label-text mb-1 text-sm font-medium"
					>Default account for new bills and expenses</span
				>
				<select
					name="defaultAccountId"
					class="select-bordered select min-h-12 w-full"
					defaultValue={form?.values?.defaultAccountId ?? hh.defaultAccountId ?? ''}
				>
					<option value="">None</option>
					{#each data.accounts as a (a.id)}
						<option value={a.id}>{a.name} · {a.institution}</option>
					{/each}
				</select>
			</label>

			<button class="btn min-h-12 rounded-full btn-primary">Save</button>
		</form>
	</section>

	<!-- Pay period -->
	<section
		id="payperiod"
		class="card scroll-mt-20 border border-base-300/60 bg-base-100 shadow-sm"
		aria-labelledby="pay"
	>
		<form method="POST" action="?/payperiod" class="card-body gap-3 p-5" use:enhance>
			<div>
				<h2 id="pay" class="font-semibold">Pay period</h2>
				<p class="text-xs text-base-content/60">
					Drives the "Until payday" card: what's due and what's safe to spend before the next
					paycheck.
				</p>
			</div>
			{#if errorFor('payperiod')}
				<div class="alert text-sm alert-error" role="alert">{errorFor('payperiod')}</div>
			{/if}
			<div class="form-control">
				<span class="label-text mb-1 text-sm font-medium">How often are you paid?</span>
				<div class="grid grid-cols-2 gap-2" role="radiogroup" aria-label="Pay period">
					{#each PAY_PERIODS as p (p.value)}
						<label
							class="btn min-h-11 justify-start rounded-xl px-3 text-left text-sm font-medium {payPeriod ===
							p.value
								? 'btn-primary'
								: 'border border-base-300 btn-ghost'}"
						>
							<input
								type="radio"
								name="payPeriod"
								value={p.value}
								bind:group={payPeriod}
								class="sr-only"
							/>
							{p.label}
						</label>
					{/each}
				</div>
			</div>
			<label class="form-control">
				<span class="label-text mb-1 text-sm font-medium">A recent payday</span>
				<input
					name="payAnchorDate"
					type="date"
					class="input-bordered input min-h-12 w-full"
					bind:value={anchor}
				/>
				<span class="label-text-alt mt-1 text-base-content/60">
					Any payday works; we count forward from it.
				</span>
			</label>
			<p class="text-sm" aria-live="polite" data-testid="payday-preview">
				{#if preview}
					<Icon name="calendar-clock" size={16} class="inline align-text-bottom text-secondary" />
					Next payday: <strong>{formatWeekdayDate(preview.date)}</strong>
					<span class="text-base-content/60">
						({preview.days === 1 ? 'tomorrow' : `in ${preview.days} days`})
					</span>
				{:else}
					<span class="text-base-content/60">Pick a payday to preview the next one.</span>
				{/if}
			</p>
			<button class="btn min-h-12 rounded-full btn-primary">Save pay period</button>
		</form>
	</section>

	<!-- Weekly spending target (v4) -->
	<section
		id="weekly"
		class="card scroll-mt-20 border border-base-300/60 bg-base-100 shadow-sm"
		aria-labelledby="weeklyh"
	>
		<form method="POST" action="?/weekly" class="card-body gap-3 p-5" use:enhance>
			<div>
				<h2 id="weeklyh" class="font-semibold">Weekly spending target</h2>
				<p class="text-xs text-base-content/60">
					Everything you spend in a week – expenses plus bills as they're paid – against one number.
					Shown on the week view and the dashboard.
				</p>
			</div>
			{#if errorFor('weekly')}
				<div class="alert text-sm alert-error" role="alert">{errorFor('weekly')}</div>
			{/if}
			<label class="form-control">
				<span class="label-text mb-1 text-sm font-medium">Target per week</span>
				<div class="input-bordered input flex min-h-12 w-full items-center gap-2">
					<span class="text-base-content/50">{currencySymbol()}</span>
					<input
						name="weeklySpend"
						type="text"
						inputmode="decimal"
						class="grow tabular"
						placeholder="Blank for none"
						aria-label="Weekly spending target"
						defaultValue={form?.values?.weeklySpend ??
							(hh.weeklySpendCents !== null && hh.weeklySpendCents !== undefined
								? centsToInput(hh.weeklySpendCents)
								: '')}
					/>
				</div>
				<span class="label-text-alt mt-1 text-base-content/60">
					Leave blank to clear it. Per-category weekly amounts live on the
					<a href="/week" class="link link-primary">week view</a>.
				</span>
			</label>
			<button class="btn min-h-12 rounded-full btn-primary">Save weekly target</button>
		</form>
	</section>

	<!-- Time zone -->
	<section class="card border border-base-300/60 bg-base-100 shadow-sm" aria-labelledby="tz">
		<form method="POST" action="?/timezone" class="card-body gap-3 p-5" use:enhance>
			<div class="flex items-center justify-between gap-3">
				<div>
					<h2 id="tz" class="font-semibold">Time zone</h2>
					<p class="text-xs text-base-content/60">
						Reminders and autopay run on this clock. Detected automatically from your phone.
					</p>
				</div>
				<Icon name="globe" size={20} class="text-base-content/50" />
			</div>
			{#if errorFor('timezone')}
				<div class="alert text-sm alert-error" role="alert">{errorFor('timezone')}</div>
			{/if}
			<p class="text-sm">
				Current: <strong data-testid="timezone">{hh.timezone}</strong>
			</p>
			{#if detectedTz && detectedTz !== hh.timezone}
				<input type="hidden" name="timezone" value={detectedTz} />
				<button class="btn min-h-11 rounded-full border border-base-300 btn-ghost">
					Use this device's time zone ({detectedTz})
				</button>
			{/if}
		</form>
	</section>
</main>
