<script lang="ts">
	import { untrack } from 'svelte';
	import { enhance } from '$app/forms';
	import type { PayPeriod } from '#lib/types.ts';
	import { AUTO_MATCH_LEVELS, COMMON_CURRENCIES, PAY_PERIODS } from '#lib/types.ts';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import HelperText from '#lib/components/HelperText.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import InstallHint from '#lib/components/InstallHint.svelte';
	import NotificationsCard from '#lib/components/NotificationsCard.svelte';
	import ThemePicker from '#lib/components/ThemePicker.svelte';
	import { centsToInput, currencySymbol, formatMoney } from '#lib/utils/money.ts';
	import {
		diffDays,
		formatWeekdayDate,
		isValidISODate,
		relativeTime,
		today
	} from '#lib/utils/dates.ts';
	import { nextPayday } from '#lib/utils/payperiod.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data, form }: PageProps = $props();

	const hh = $derived(data.household);
	const f = $derived(form as Record<string, unknown> | null);
	const errorFor = (section: string) =>
		f?.section === section && typeof f.error === 'string' ? f.error : undefined;
	const valuesFor = (section: string) =>
		f?.section === section ? ((f.values as Record<string, string> | undefined) ?? {}) : {};

	let changingPassword = $state(false);
	const initial = untrack(() => ({
		payPeriod: (data.household?.payPeriod ?? 'biweekly') as PayPeriod,
		anchor: data.household?.payAnchorDate ?? '',
		currency: data.household?.currency ?? 'USD'
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
		if (!f) return;
		if (f.profileSaved) toasts.show('Name updated', 'success');
		if (f.passwordSaved) {
			toasts.show('Password changed', 'success');
			changingPassword = false;
		}
		if (f.saved && f.section) toasts.show('Saved', 'success');
		if (f.rematched) toasts.show('Re-checking everything in To review…', 'success');
		if (typeof f.rematchError === 'string') toasts.show(f.rematchError, 'error');
	});

	const bankLine = $derived.by(() => {
		if (data.banks === null) return 'Not set up on this server';
		if (data.banks.length === 0) return 'Pull balances and transactions with SimpleFIN';
		const broken = data.banks.filter((b) => b.lastError).length;
		return `${data.banks.length} linked${broken ? ` · ${broken} need${broken === 1 ? 's' : ''} attention` : ''}`;
	});
	const matcherLine = $derived.by(() => {
		const m = data.matcher;
		if (!m) return null;
		const engine =
			m.engine === 'fuzzy' && m.reachable
				? 'Smart matching is on'
				: 'Using the built-in matcher (smart matching is offline)';
		const rt = m.lastRunAt ? relativeTime(m.lastRunAt) : '';
		const last = !rt
			? ''
			: rt === 'just now'
				? ' · checked just now'
				: /^\d+[mhd]$/.test(rt)
					? ` · checked ${rt} ago`
					: ` · checked ${rt}`;
		const auto = m.lastAutoMatched ? ` · ${m.lastAutoMatched} matched by itself` : '';
		const note = m.note ? ` · ${m.note}` : '';
		return `${engine}${last}${auto}${note}`;
	});
	const partner = $derived(hh?.members.find((m) => m.id !== data.user?.id) ?? null);
	const level = $derived(hh?.autoMatchLevel ?? 'all');

	const card = 'card border border-base-300/60 bg-base-100 shadow-sm';
	const row = 'flex min-h-14 items-center gap-3 rounded-xl px-1 py-2 hover:bg-base-200';
	const rowIcon =
		'flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-base-200 text-primary';
	const keep =
		() =>
		async ({ update }: { update: (o?: { reset?: boolean }) => Promise<void> }) =>
			update({ reset: false });
</script>

<svelte:head><title>Settings · CouplesCash</title></svelte:head>

<AppBar title="Settings" back="/more" />

<main class="flex flex-col gap-4 px-4 pt-2">
	<HelperText
		id="settings"
		text="Everything you can change, on one page: you, your household, your banks."
	/>

	<nav class="flex gap-2" aria-label="Settings sections">
		{#each [{ id: 'you', l: 'You' }, ...(hh ? [{ id: 'household', l: 'Household' }, { id: 'banks', l: 'Banks' }] : [])] as s (s.id)}
			<a
				href="#{s.id}"
				class="btn min-h-10 rounded-full border border-base-300 btn-ghost px-4 btn-sm">{s.l}</a
			>
		{/each}
	</nav>

	<!-- ===== You ===== -->
	<section id="you" class="{card} scroll-mt-20" aria-labelledby="you-h">
		<div class="card-body gap-5 p-5">
			<h2 id="you-h" class="text-lg font-semibold">You</h2>

			<form method="POST" action="?/profile" class="flex flex-col gap-1" use:enhance={keep}>
				<label class="form-control">
					<span class="label-text mb-1 text-sm font-medium">Your name</span>
					<div class="flex gap-2">
						<input
							name="displayName"
							type="text"
							required
							maxlength="40"
							class="input-bordered input min-h-12 flex-1"
							defaultValue={data.me.user.displayName}
						/>
						<button class="btn min-h-12 rounded-xl btn-secondary">Save</button>
					</div>
				</label>
				{#if typeof f?.profileError === 'string'}<p class="text-sm text-error" role="alert">
						{f.profileError}
					</p>{/if}
				<p class="text-xs text-base-content/60">Signed in as {data.me.user.email}</p>
			</form>

			<div class="flex flex-col gap-2">
				<p class="text-sm font-medium">Theme</p>
				<ThemePicker theme={data.theme} />
			</div>

			<NotificationsCard
				vapidPublicKey={data.vapidPublicKey}
				initialPrefs={data.prefs}
				form={f as never}
			/>

			<div class="flex flex-col">
				<a href="/settings/passkeys" class={row}>
					<span class={rowIcon}><Icon name="key-round" size={20} /></span>
					<span class="flex-1">
						<span class="block font-medium">Passkeys</span>
						<span class="block text-xs text-base-content/60">Sign in with Face ID or Touch ID</span>
					</span>
					<Icon name="chevron-right" size={18} class="text-base-content/40" />
				</a>
				<button
					type="button"
					class="{row} w-full text-left"
					aria-expanded={changingPassword}
					onclick={() => (changingPassword = !changingPassword)}
				>
					<span class={rowIcon}><Icon name="shield" size={20} /></span>
					<span class="flex-1">
						<span class="block font-medium">Password</span>
						<span class="block text-xs text-base-content/60"
							>Changing it signs out every other device</span
						>
					</span>
					<Icon
						name={changingPassword ? 'chevron-up' : 'chevron-down'}
						size={18}
						class="text-base-content/40"
					/>
				</button>
				{#if changingPassword}
					<form method="POST" action="?/password" class="flex flex-col gap-3 px-1 pt-2" use:enhance>
						<label class="form-control">
							<span class="label-text mb-1 text-sm font-medium">Current password</span>
							<input
								name="currentPassword"
								type="password"
								autocomplete="current-password"
								required
								class="input-bordered input min-h-12 w-full"
							/>
						</label>
						<label class="form-control">
							<span class="label-text mb-1 text-sm font-medium">New password</span>
							<input
								name="newPassword"
								type="password"
								autocomplete="new-password"
								required
								minlength="10"
								class="input-bordered input min-h-12 w-full"
							/>
						</label>
						<label class="form-control">
							<span class="label-text mb-1 text-sm font-medium">Confirm new password</span>
							<input
								name="confirmPassword"
								type="password"
								autocomplete="new-password"
								required
								minlength="10"
								class="input-bordered input min-h-12 w-full"
							/>
						</label>
						{#if typeof f?.passwordError === 'string'}<p class="text-sm text-error" role="alert">
								{f.passwordError}
							</p>{/if}
						<button class="btn min-h-12 rounded-full btn-primary">Update password</button>
					</form>
				{/if}
			</div>

			<details class="group rounded-xl">
				<summary class="{row} cursor-pointer list-none">
					<span class={rowIcon}><Icon name="smartphone" size={20} /></span>
					<span class="flex-1 font-medium">Install the app</span>
					<Icon name="chevron-down" size={18} class="text-base-content/40 group-open:rotate-180" />
				</summary>
				<div class="px-1 pt-2"><InstallHint /></div>
			</details>
		</div>
	</section>

	{#if hh}
		<!-- ===== Household ===== -->
		<section id="household" class="{card} scroll-mt-20" aria-labelledby="household-h">
			<div class="card-body gap-5 p-5">
				<h2 id="household-h" class="text-lg font-semibold">Household</h2>

				<form method="POST" action="?/household" class="flex flex-col gap-3" use:enhance={keep}>
					{#if errorFor('household')}
						<div class="alert text-sm alert-error" role="alert">{errorFor('household')}</div>
					{/if}
					<label class="form-control">
						<span class="label-text mb-1 text-sm font-medium">Household name</span>
						<input
							name="name"
							type="text"
							required
							maxlength="40"
							class="input-bordered input min-h-12 w-full"
							defaultValue={valuesFor('household').name ?? hh.name}
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
									defaultValue={COMMON_CURRENCIES.some((c) => c.code === hh.currency)
										? ''
										: hh.currency}
								/>
							</label>
						{:else}
							<div class="form-control">
								<span class="label-text mb-1 text-sm font-medium">Looks like</span>
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
									class="btn join-item min-h-11 flex-1 border border-base-300 btn-ghost has-checked:border-primary has-checked:bg-primary has-checked:text-primary-content"
								>
									<input
										type="radio"
										name="weekStart"
										value={o.v}
										class="sr-only"
										defaultChecked={String(hh.weekStart) === o.v}
									/>
									{o.l}
								</label>
							{/each}
						</div>
					</div>
					<label class="form-control">
						<span class="label-text mb-1 text-sm font-medium">Default account for new bills</span>
						<select
							name="defaultAccountId"
							class="select-bordered select min-h-12 w-full"
							defaultValue={hh.defaultAccountId ?? ''}
						>
							<option value="">None</option>
							{#each data.accounts as a (a.id)}
								<option value={a.id}>{a.name} · {a.institution}</option>
							{/each}
						</select>
					</label>
					<button class="btn min-h-12 rounded-full btn-primary">Save household</button>
				</form>

				<!-- Pay period -->
				<form
					id="payperiod"
					method="POST"
					action="?/payperiod"
					class="flex scroll-mt-20 flex-col gap-3 border-t border-base-300/60 pt-5"
					use:enhance={keep}
				>
					<div>
						<h3 class="font-semibold">Pay period</h3>
						<p class="text-xs text-base-content/60">
							Shows "Next payday" on the week and counts the paycheck in.
						</p>
					</div>
					{#if errorFor('payperiod')}
						<div class="alert text-sm alert-error" role="alert">{errorFor('payperiod')}</div>
					{/if}
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
					<label class="form-control">
						<span class="label-text mb-1 text-sm font-medium">A recent payday</span>
						<input
							name="payAnchorDate"
							type="date"
							class="input-bordered input min-h-12 w-full"
							bind:value={anchor}
						/>
					</label>
					<p class="text-sm" aria-live="polite" data-testid="payday-preview">
						{#if preview}
							<Icon
								name="calendar-clock"
								size={16}
								class="inline align-text-bottom text-secondary"
							/>
							Next payday: <strong>{formatWeekdayDate(preview.date)}</strong>
							<span class="text-base-content/60"
								>({preview.days === 1 ? 'tomorrow' : `in ${preview.days} days`})</span
							>
						{:else}
							<span class="text-base-content/60">Pick a payday to preview the next one.</span>
						{/if}
					</p>
					<button class="btn min-h-12 rounded-full btn-primary">Save pay period</button>
				</form>

				<!-- Weekly target -->
				<form
					id="weekly"
					method="POST"
					action="?/weekly"
					class="flex scroll-mt-20 flex-col gap-3 border-t border-base-300/60 pt-5"
					use:enhance={keep}
				>
					<div>
						<h3 class="font-semibold">Weekly spending target</h3>
						<p class="text-xs text-base-content/60">
							One number for everything you spend in a week. Per-category amounts live on the
							<a href="/week" class="link link-primary">Week</a>.
						</p>
					</div>
					{#if errorFor('weekly')}
						<div class="alert text-sm alert-error" role="alert">{errorFor('weekly')}</div>
					{/if}
					<div class="flex gap-2">
						<label class="input-bordered input flex min-h-12 flex-1 items-center gap-2">
							<span class="text-base-content/50">{currencySymbol()}</span>
							<input
								name="weeklySpend"
								type="text"
								inputmode="decimal"
								class="grow tabular"
								placeholder="Blank for none"
								aria-label="Weekly spending target"
								defaultValue={valuesFor('weekly').weeklySpend ??
									(hh.weeklySpendCents != null ? centsToInput(hh.weeklySpendCents) : '')}
							/>
						</label>
						<button class="btn min-h-12 rounded-xl btn-secondary">Save</button>
					</div>
				</form>

				<!-- Partner -->
				<a href="/couple" class="{row} border-t border-base-300/60 pt-4">
					<span class={rowIcon}><Icon name="heart-handshake" size={20} /></span>
					<span class="flex-1">
						<span class="block font-medium">Partner</span>
						<span class="block text-xs text-base-content/60" data-testid="partner-line">
							{partner
								? `Shared with ${partner.displayName}`
								: 'Invite your partner with a Couple Code'}
						</span>
					</span>
					<Icon name="chevron-right" size={18} class="text-base-content/40" />
				</a>

				<!-- Matching -->
				<form
					id="matching"
					method="POST"
					action="?/matching"
					class="flex scroll-mt-20 flex-col gap-3 border-t border-base-300/60 pt-5"
					use:enhance={keep}
				>
					<div>
						<h3 class="font-semibold">Matching</h3>
						<p class="text-xs text-base-content/60">
							How much the app may do by itself when transactions arrive. Everything it does shows
							an "auto" mark and can be undone.
						</p>
					</div>
					{#if errorFor('matching')}
						<div class="alert text-sm alert-error" role="alert">{errorFor('matching')}</div>
					{/if}
					<div class="flex flex-col gap-2" role="radiogroup" aria-label="Auto-match">
						{#each AUTO_MATCH_LEVELS as o (o.value)}
							<label
								class="flex min-h-14 cursor-pointer items-center gap-3 rounded-xl border border-base-300 px-3 py-2 has-checked:border-primary has-checked:bg-primary/5"
							>
								<input
									type="radio"
									name="autoMatchLevel"
									value={o.value}
									class="radio radio-sm radio-primary"
									defaultChecked={level === o.value}
									onchange={(e) => e.currentTarget.form?.requestSubmit()}
								/>
								<span class="flex-1">
									<span class="block text-sm font-medium">{o.label}</span>
									<span class="block text-xs text-base-content/60">{o.hint}</span>
								</span>
							</label>
						{/each}
					</div>
					<input type="hidden" name="autoIgnoreTransfersForm" value="1" />
					<label class="flex min-h-11 cursor-pointer items-center justify-between gap-3">
						<span class="text-sm font-medium"
							>Automatically ignore transfers between linked accounts</span
						>
						<input
							type="checkbox"
							name="autoIgnoreTransfers"
							class="toggle toggle-primary"
							defaultChecked={hh.autoIgnoreTransfers !== false}
							onchange={(e) => e.currentTarget.form?.requestSubmit()}
						/>
					</label>
					<noscript>
						<button class="btn min-h-11 rounded-full border border-base-300 btn-ghost">Save</button>
					</noscript>
				</form>

				<!-- Time zone -->
				<form
					method="POST"
					action="?/household"
					class="flex flex-wrap items-center gap-2 border-t border-base-300/60 pt-4 text-sm"
					use:enhance={keep}
				>
					<Icon name="globe" size={16} class="text-base-content/50" />
					<span class="flex-1">Time zone <strong data-testid="timezone">{hh.timezone}</strong></span
					>
					{#if detectedTz && detectedTz !== hh.timezone}
						<input type="hidden" name="timezone" value={detectedTz} />
						<button class="btn min-h-11 rounded-full border border-base-300 btn-ghost btn-sm">
							Use {detectedTz}
						</button>
					{/if}
				</form>
			</div>
		</section>

		<!-- ===== Banks ===== -->
		<section id="banks" class="{card} scroll-mt-20" aria-labelledby="banks-h">
			<div class="card-body gap-3 p-5">
				<h2 id="banks-h" class="text-lg font-semibold">Banks</h2>
				<div class="flex flex-col">
					<a href="/settings/banks" class={row}>
						<span class={rowIcon}><Icon name="landmark" size={20} /></span>
						<span class="flex-1">
							<span class="block font-medium">Linked banks</span>
							<span class="block text-xs text-base-content/60" data-testid="banks-line"
								>{bankLine}</span
							>
						</span>
						<Icon name="chevron-right" size={18} class="text-base-content/40" />
					</a>
					<a href="/imports/csv" class={row}>
						<span class={rowIcon}><Icon name="file-spreadsheet" size={20} /></span>
						<span class="flex-1">
							<span class="block font-medium">Import a CSV</span>
							<span class="block text-xs text-base-content/60"
								>{data.csvCount
									? `${data.csvCount} imported so far`
									: 'A statement export from your bank'}</span
							>
						</span>
						<Icon name="chevron-right" size={18} class="text-base-content/40" />
					</a>
				</div>
				<form
					method="POST"
					action="?/rematch"
					class="flex flex-col gap-2 rounded-2xl bg-base-200/50 p-3"
					use:enhance={keep}
				>
					{#if matcherLine}
						<p
							class="flex items-center gap-2 text-xs text-base-content/70"
							data-testid="matcher-status"
						>
							<Icon name="wand-sparkles" size={14} class="text-secondary" />
							{matcherLine}
						</p>
					{/if}
					<button
						class="btn min-h-11 rounded-full border border-base-300 btn-ghost btn-sm"
						data-testid="rematch"
					>
						<Icon name="refresh-cw" size={16} /> Re-check matches now
					</button>
				</form>
			</div>
		</section>
	{:else}
		<a href="/couple" class="{card} text-sm">
			<div class="card-body flex-row items-center gap-3 p-4">
				<Icon name="heart-handshake" size={20} class="text-primary" />
				<span class="flex-1">Household settings appear once you set up your household.</span>
				<Icon name="chevron-right" size={18} class="text-base-content/40" />
			</div>
		</a>
	{/if}

	<form method="POST" action="?/signout" use:enhance>
		<button class="btn min-h-12 w-full rounded-full btn-outline btn-error">
			<Icon name="log-out" size={18} /> Sign out
		</button>
	</form>

	<p class="pb-2 text-center text-xs text-base-content/40">CouplesCash · made for two</p>
</main>
