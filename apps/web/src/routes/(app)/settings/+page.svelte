<script lang="ts">
	import { enhance } from '$app/forms';
	import { AUTO_MATCH_LEVELS } from '#lib/types.ts';
	import type { PageProps } from './$types';
	import AccountRolePicker from '#lib/components/AccountRolePicker.svelte';
	import AppBar from '#lib/components/AppBar.svelte';
	import FunMoneyFields from '#lib/components/FunMoneyFields.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import InstallHint from '#lib/components/InstallHint.svelte';
	import NotificationsCard from '#lib/components/NotificationsCard.svelte';
	import PaydayFields from '#lib/components/PaydayFields.svelte';
	import ThemePicker from '#lib/components/ThemePicker.svelte';
	import { relativeTime } from '#lib/utils/dates.ts';
	import { feedAdvice, feedSummary, firstName, weekdayName } from '#lib/utils/plan.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data, form }: PageProps = $props();

	const hh = $derived(data.household);
	const f = $derived(form as Record<string, unknown> | null);
	const errorFor = (section: string) =>
		f?.section === section && typeof f.error === 'string' ? f.error : undefined;

	let changingPassword = $state(false);
	let detectedTz = $state('');
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
	const paydayWd = $derived(hh?.paydayWeekday ?? 5);
	const incomeCategoryId = $derived(
		data.categories.find((c) => c.name.toLowerCase() === 'income')?.id ?? null
	);
	/** pending rows we show per account (bills account from the plan; others unknown) */
	const shownPending = (accountId: string | null) =>
		accountId && accountId === data.plan?.billsAccount?.id
			? data.plan.billsAccount.pending.length
			: null;
	const feeds = $derived(
		(data.banks ?? []).flatMap((l) =>
			l.accounts.map((x) => ({ link: l, ext: x, summary: feedSummary(x.lastFeed) }))
		)
	);

	const card = 'card border border-base-300/60 bg-base-100 shadow-sm';
	const row = 'flex min-h-14 items-center gap-3 rounded-xl px-1 py-2 hover:bg-base-200';
	const rowIcon =
		'flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-base-200 text-primary';
	const keep =
		() =>
		async ({ update }: { update: (o?: { reset?: boolean }) => Promise<void> }) =>
			update({ reset: false });
	const sections = $derived([
		{ id: 'you', l: 'You' },
		...(hh
			? [
					{ id: 'household', l: 'Household' },
					{ id: 'accounts', l: 'Accounts' },
					{ id: 'banks', l: 'Banks' }
				]
			: [])
	]);
</script>

<svelte:head><title>Settings · CouplesCash</title></svelte:head>

<AppBar title="Settings" back="/week" />

<main class="flex flex-col gap-4 px-4 pt-2 pb-4">
	<nav class="-mx-4 flex gap-2 overflow-x-auto px-4" aria-label="Settings sections">
		{#each sections as s (s.id)}
			<a
				href="#{s.id}"
				class="btn min-h-10 shrink-0 rounded-full border border-base-300 btn-ghost px-4 btn-sm"
				>{s.l}</a
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
							class="input-bordered input min-h-12 min-w-0 flex-1"
							defaultValue={data.me.user.displayName}
						/>
						<button class="btn min-h-12 rounded-xl btn-secondary">Save</button>
					</div>
				</label>
				{#if typeof f?.profileError === 'string'}<p class="text-sm text-error" role="alert">
						{f.profileError}
					</p>{/if}
				<p class="truncate text-xs text-base-content/60">Signed in as {data.me.user.email}</p>
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
				<details class="group rounded-xl">
					<summary class="{row} cursor-pointer list-none">
						<span class={rowIcon}><Icon name="smartphone" size={20} /></span>
						<span class="flex-1 font-medium">Install the app</span>
						<Icon
							name="chevron-down"
							size={18}
							class="text-base-content/40 group-open:rotate-180"
						/>
					</summary>
					<div class="px-1 pt-2"><InstallHint /></div>
				</details>
			</div>
		</div>
	</section>

	{#if hh}
		<!-- ===== Household ===== -->
		<section id="household" class="{card} scroll-mt-20" aria-labelledby="household-h">
			<div class="card-body gap-5 p-5">
				<h2 id="household-h" class="text-lg font-semibold">Household</h2>

				<a href="/couple" class={row}>
					<span class={rowIcon}><Icon name="heart-handshake" size={20} /></span>
					<span class="min-w-0 flex-1">
						<span class="block font-medium">Partner</span>
						<span class="block truncate text-xs text-base-content/60" data-testid="partner-line">
							{partner
								? `Shared with ${firstName(partner.displayName)}`
								: 'Invite your partner with a Couple Code'}
						</span>
					</span>
					<Icon name="chevron-right" size={18} class="text-base-content/40" />
				</a>

				<form
					id="payday"
					method="POST"
					action="?/payday"
					class="flex scroll-mt-20 flex-col gap-3 border-t border-base-300/60 pt-5"
					use:enhance={keep}
					data-testid="settings-payday"
				>
					{#if typeof f?.paydayError === 'string'}
						<div class="alert text-sm alert-error" role="alert">{f.paydayError}</div>
					{/if}
					<PaydayFields
						weekday={paydayWd}
						paycheckCents={data.paycheck?.amountCents ?? null}
						found={data.paycheck?.source === 'found' ? data.paycheck : null}
						{incomeCategoryId}
					/>
					<button class="btn min-h-12 rounded-full btn-primary">Save payday</button>
				</form>

				<form
					id="fun"
					method="POST"
					action="?/fun"
					class="flex scroll-mt-20 flex-col gap-3 border-t border-base-300/60 pt-5"
					use:enhance={keep}
					data-testid="settings-fun"
				>
					<h3 class="font-semibold">Fun money</h3>
					{#if typeof f?.funError === 'string'}
						<div class="alert text-sm alert-error" role="alert">{f.funError}</div>
					{/if}
					<FunMoneyFields rows={data.funRows} paydayName={weekdayName(paydayWd)} />
					{#if data.funRows.length}
						<button class="btn min-h-12 rounded-full btn-primary">Save fun money</button>
					{/if}
				</form>

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
					<noscript>
						<button class="btn min-h-11 rounded-full border border-base-300 btn-ghost">Save</button>
					</noscript>
				</form>

				<form
					method="POST"
					action="?/household"
					class="flex flex-wrap items-center gap-2 border-t border-base-300/60 pt-4 text-sm"
					use:enhance={keep}
				>
					<Icon name="globe" size={16} class="text-base-content/50" />
					<span class="min-w-0 flex-1"
						>Time zone <strong data-testid="timezone">{hh.timezone}</strong></span
					>
					{#if detectedTz && detectedTz !== hh.timezone}
						<input type="hidden" name="timezone" value={detectedTz} />
						<button class="btn min-h-11 rounded-full border border-base-300 btn-ghost btn-sm">
							Use {detectedTz}
						</button>
					{/if}
				</form>

				<a href="/setup" class="{row} border-t border-base-300/60 pt-4" data-testid="rerun-setup">
					<span class={rowIcon}><Icon name="list-checks" size={20} /></span>
					<span class="flex-1">
						<span class="block font-medium">Set up again</span>
						<span class="block text-xs text-base-content/60"
							>Banks, accounts, payday, fun money, notes, partner</span
						>
					</span>
					<Icon name="chevron-right" size={18} class="text-base-content/40" />
				</a>
			</div>
		</section>

		<!-- ===== Accounts ===== -->
		<section id="accounts" class="{card} scroll-mt-20" aria-labelledby="accounts-h">
			<div class="card-body gap-3 p-5">
				<div>
					<h2 id="accounts-h" class="text-lg font-semibold">Accounts</h2>
					<p class="text-xs text-base-content/60">
						Which account is which. Only the bills account counts in the weekly math.
					</p>
				</div>
				{#if data.rows.length === 0}
					<p class="text-sm text-base-content/65">
						No accounts yet. Link your banks below, or add one by hand.
					</p>
				{:else}
					<form method="POST" action="?/roles" class="flex flex-col gap-3" use:enhance={keep}>
						{#if typeof f?.rolesError === 'string'}
							<div class="alert text-sm alert-error" role="alert">{f.rolesError}</div>
						{/if}
						<AccountRolePicker rows={data.rows} defaults={data.defaults} members={hh.members} />
						<button class="btn min-h-12 rounded-full btn-primary" data-testid="save-roles"
							>Save accounts</button
						>
					</form>
				{/if}
				<a href="/accounts/new" class="{row} border-t border-base-300/60 pt-3">
					<span class={rowIcon}><Icon name="plus" size={20} /></span>
					<span class="flex-1">
						<span class="block font-medium">Add an account by hand</span>
						<span class="block text-xs text-base-content/60">Cash, or a bank sync can't reach</span>
					</span>
					<Icon name="chevron-right" size={18} class="text-base-content/40" />
				</a>
			</div>
		</section>

		<!-- ===== Banks ===== -->
		<section id="banks" class="{card} scroll-mt-20" aria-labelledby="banks-h">
			<div class="card-body gap-3 p-5">
				<h2 id="banks-h" class="text-lg font-semibold">Banks</h2>
				<a href="/settings/banks" class={row}>
					<span class={rowIcon}><Icon name="landmark" size={20} /></span>
					<span class="min-w-0 flex-1">
						<span class="block font-medium">Linked banks</span>
						<span class="block truncate text-xs text-base-content/60" data-testid="banks-line"
							>{data.banks === null
								? 'Not set up on this server'
								: data.banks.length === 0
									? 'Pull balances and transactions with SimpleFIN'
									: `${data.banks.length} linked${data.banks.some((b) => b.lastError) ? ' · needs attention' : ''}`}</span
						>
					</span>
					<Icon name="chevron-right" size={18} class="text-base-content/40" />
				</a>

				{#if feeds.length}
					<div
						class="flex flex-col gap-2 rounded-2xl bg-base-200/50 p-3"
						data-testid="feed-diagnostics"
					>
						<p class="text-sm font-medium">Last sync</p>
						<ul class="flex flex-col gap-2">
							{#each feeds as fd (fd.ext.id)}
								{@const advice = feedAdvice(fd.ext.lastFeed, shownPending(fd.ext.accountId))}
								<li class="text-xs" data-testid="feed-line">
									<p>
										<span class="font-semibold">{fd.ext.accountName ?? fd.ext.name}</span>
										{fd.summary ?? 'no sync since the update yet'}
										{#if fd.ext.lastFeed?.at}
											<span class="text-base-content/50">
												· {relativeTime(fd.ext.lastFeed.at)}</span
											>
										{/if}
									</p>
									{#if advice && fd.ext.accountId === data.plan?.billsAccount?.id}
										<p class="mt-0.5 text-base-content/60" data-testid="feed-advice">{advice}</p>
									{/if}
								</li>
							{/each}
						</ul>
					</div>
				{/if}

				<a href="/imports/csv" class={row}>
					<span class={rowIcon}><Icon name="file-spreadsheet" size={20} /></span>
					<span class="flex-1">
						<span class="block font-medium">Import a statement</span>
						<span class="block text-xs text-base-content/60"
							>{data.csvCount
								? `${data.csvCount} imported so far`
								: 'A CSV export, when the bank feed misses something'}</span
						>
					</span>
					<Icon name="chevron-right" size={18} class="text-base-content/40" />
				</a>
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
							<Icon name="wand-sparkles" size={14} class="shrink-0 text-secondary" />
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
