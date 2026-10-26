<script lang="ts">
	import { enhance } from '$app/forms';
	import { untrack } from 'svelte';
	import type { PageProps } from './$types';
	import AccountRolePicker from '#lib/components/AccountRolePicker.svelte';
	import AppBar from '#lib/components/AppBar.svelte';
	import FunMoneyFields from '#lib/components/FunMoneyFields.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import NotesImport from '#lib/components/NotesImport.svelte';
	import PaydayFields from '#lib/components/PaydayFields.svelte';
	import { firstName, weekdayName } from '#lib/utils/plan.ts';
	import { SETUP_STEPS, nextStep, prevStep } from '#lib/utils/setup.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data, form }: PageProps = $props();

	const f = $derived(form as Record<string, unknown> | null);
	const index = $derived(SETUP_STEPS.findIndex((s) => s.key === data.step));
	const meta = $derived(SETUP_STEPS[index]);
	const next = $derived(nextStep(data.step));
	const prev = $derived(prevStep(data.step));
	const skipHref = $derived(next ? `/setup?step=${next}` : '/week');
	const hh = $derived(data.household);
	const paydayWd = $derived(hh?.paydayWeekday ?? 5);
	const paydayName = $derived(weekdayName(paydayWd));
	const partner = $derived(hh?.members.find((m) => m.id !== data.user?.id) ?? null);
	const links = $derived(data.banks ?? []);
	const savingsId = $derived(data.accounts.find((a) => a.role === 'savings')?.id ?? null);
	const billsId = $derived(
		data.plan?.billsAccount?.id ?? data.accounts.find((a) => a.role === 'bills')?.id ?? null
	);
	const incomeCategoryId = $derived(
		data.categories.find((c) => c.name.toLowerCase() === 'income')?.id ?? null
	);
	let busy = $state(false);
	const submit = () => {
		busy = true;
		return async ({ update }: { update: (o?: { reset?: boolean }) => Promise<void> }) => {
			await update({ reset: false });
			busy = false;
		};
	};
	$effect(() => {
		if (data.added)
			untrack(() =>
				toasts.show(`${data.added} bill${data.added === 1 ? '' : 's'} added`, 'success')
			);
	});
	$effect(() => {
		const linked = data.linked;
		if (linked)
			untrack(() =>
				toasts.show(`Linked ${linked.name} · ${linked.found} accounts found`, 'success')
			);
	});
	const card = 'card border border-base-300/60 bg-base-100 shadow-sm';
</script>

<svelte:head><title>Setup · CouplesCash</title></svelte:head>

<AppBar
	title="Set up"
	subtitle="Step {index + 1} of {SETUP_STEPS.length}"
	back="/week"
	gear={false}
/>

<main class="flex flex-col gap-4 px-4 pt-1 pb-6">
	<ol class="flex gap-1" aria-label="Steps">
		{#each SETUP_STEPS as s, i (s.key)}
			<li class="flex-1">
				<a
					href="/setup?step={s.key}"
					class="block h-1.5 rounded-full {i === index
						? 'bg-primary'
						: data.done[s.key]
							? 'bg-success/70'
							: 'bg-base-300'}"
					aria-label="{i + 1}. {s.title}{data.done[s.key] ? ' (done)' : ''}"
					aria-current={i === index ? 'step' : undefined}
				></a>
			</li>
		{/each}
	</ol>

	<section class={card} aria-labelledby="step-h" data-testid="setup-step" data-step={data.step}>
		<div class="card-body gap-4 p-5">
			<div>
				<h2 id="step-h" class="text-lg font-semibold">{index + 1}. {meta.title}</h2>
				<p class="text-sm text-base-content/65">{meta.hint}</p>
			</div>

			{#if data.step === 'banks'}
				{#if data.banks === null || f?.notConfigured}
					<div
						class="alert items-start text-sm alert-warning"
						role="status"
						data-testid="banks-not-configured"
					>
						<Icon name="triangle-alert" size={18} />
						<span
							>Bank sync isn't set up on this server yet. Skip this step and add accounts by hand,
							or ask whoever runs CouplesCash to set <code>DATA_KEY</code>.</span
						>
					</div>
				{:else}
					{#if links.length}
						<ul class="flex flex-col gap-1" data-testid="setup-links">
							{#each links as l (l.id)}
								<li class="flex items-center gap-2 rounded-xl bg-success/10 px-3 py-2 text-sm">
									<Icon name="circle-check" size={18} class="text-success" />
									<span class="flex-1 font-medium">{l.name}</span>
									<span class="text-xs text-base-content/60"
										>{l.accounts.length} account{l.accounts.length === 1 ? '' : 's'}</span
									>
								</li>
							{/each}
						</ul>
					{/if}
					<p class="text-sm text-base-content/70">
						CouplesCash reads balances and transactions through <strong>SimpleFIN Bridge</strong>
						(read-only, $1.50 a month). Connect US Bank and Ally at
						<a
							href="https://beta-bridge.simplefin.org/simplefin/create"
							target="_blank"
							rel="noopener noreferrer"
							class="link link-primary">beta-bridge.simplefin.org</a
						>, choose <strong>Setup Token</strong> and paste it here.
					</p>
					<form
						method="POST"
						action="?step=banks&/claim"
						class="flex flex-col gap-3"
						use:enhance={submit}
					>
						{#if typeof f?.bankError === 'string'}
							<div class="alert text-sm alert-error" role="alert">{f.bankError}</div>
						{/if}
						<label class="form-control">
							<span class="label-text mb-1 text-sm font-medium">Name</span>
							<input
								name="name"
								type="text"
								maxlength="40"
								class="input-bordered input min-h-12 w-full"
								defaultValue="Our banks"
							/>
						</label>
						<label class="form-control">
							<span class="label-text mb-1 text-sm font-medium">Setup token</span>
							<textarea
								name="setupToken"
								rows="2"
								class="textarea-bordered textarea w-full font-mono text-xs"
								autocomplete="off"
								autocapitalize="off"
								spellcheck="false"></textarea>
						</label>
						<button
							class="btn min-h-12 rounded-full {links.length
								? 'border border-base-300 btn-ghost'
								: 'btn-primary'}"
							disabled={busy}
						>
							{#if busy}<span class="loading loading-sm loading-spinner"></span>{/if}
							{links.length ? 'Link another bank' : 'Link bank'}
						</button>
					</form>
				{/if}
				{#if links.length}
					<a
						href="/setup?step=roles"
						class="btn min-h-12 rounded-full btn-primary"
						data-testid="setup-next">Next: which account is which</a
					>
				{/if}
			{:else if data.step === 'roles'}
				{#if data.rows.length === 0}
					<p class="text-sm text-base-content/70">
						No accounts yet. <a href="/setup?step=banks" class="link link-primary"
							>Link your banks</a
						>
						or <a href="/accounts/new" class="link link-primary">add one by hand</a>.
					</p>
				{:else}
					<form
						method="POST"
						action="?step=roles&/roles"
						class="flex flex-col gap-3"
						use:enhance={submit}
					>
						{#if typeof f?.rolesError === 'string'}
							<div class="alert text-sm alert-error" role="alert">{f.rolesError}</div>
						{/if}
						<AccountRolePicker
							rows={data.rows}
							defaults={data.defaults}
							members={hh?.members ?? []}
						/>
						<button
							class="btn min-h-12 rounded-full btn-primary"
							disabled={busy}
							data-testid="setup-save"
						>
							{#if busy}<span class="loading loading-sm loading-spinner"></span>{/if}
							Save and continue
						</button>
					</form>
				{/if}
			{:else if data.step === 'payday'}
				<form
					method="POST"
					action="?step=payday&/payday"
					class="flex flex-col gap-4"
					use:enhance={submit}
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
					<button
						class="btn min-h-12 rounded-full btn-primary"
						disabled={busy}
						data-testid="setup-save"
					>
						{#if busy}<span class="loading loading-sm loading-spinner"></span>{/if}
						Save and continue
					</button>
				</form>
			{:else if data.step === 'fun'}
				<form
					method="POST"
					action="?step=fun&/fun"
					class="flex flex-col gap-3"
					use:enhance={submit}
				>
					{#if typeof f?.funError === 'string'}
						<div class="alert text-sm alert-error" role="alert">{f.funError}</div>
					{/if}
					<FunMoneyFields rows={data.funRows} {paydayName} />
					{#if data.funRows.length}
						<button
							class="btn min-h-12 rounded-full btn-primary"
							disabled={busy}
							data-testid="setup-save"
						>
							{#if busy}<span class="loading loading-sm loading-spinner"></span>{/if}
							Save and continue
						</button>
					{/if}
				</form>
			{:else if data.step === 'notes'}
				<NotesImport
					action="?step=notes&/notes"
					categories={data.categories}
					billsAccountId={billsId}
					savingsAccountId={savingsId}
					error={typeof f?.notesError === 'string' ? f.notesError : null}
				/>
			{:else if data.step === 'partner'}
				{#if data.added}
					<p
						class="flex items-center gap-2 rounded-xl bg-success/10 px-3 py-2 text-sm"
						data-testid="notes-added"
					>
						<Icon name="circle-check" size={18} class="text-success" />
						{data.added} bill{data.added === 1 ? '' : 's'} added from your notes
					</p>
				{/if}
				{#if partner}
					<p class="flex items-center gap-2 text-sm" data-testid="partner-joined">
						<Icon name="heart-handshake" size={18} class="text-primary" />
						{firstName(partner.displayName)} has joined. You both see the same week.
					</p>
				{:else}
					<p class="text-sm text-base-content/70">
						Show your partner a Couple Code: they scan it or type it after creating their account,
						and you both see the same Friday plan.
					</p>
					<form method="POST" action="/couple?/code">
						<button
							class="btn min-h-12 w-full rounded-full border border-base-300 btn-ghost"
							data-testid="invite"
						>
							<Icon name="qr-code" size={18} /> Show a Couple Code
						</button>
					</form>
				{/if}
				<a href="/week" class="btn min-h-12 rounded-full btn-primary" data-testid="setup-finish"
					>Done, show this week</a
				>
			{/if}
		</div>
	</section>

	<div class="flex items-center justify-between gap-2">
		{#if prev}
			<a href="/setup?step={prev}" class="btn min-h-11 rounded-full btn-ghost btn-sm">
				<Icon name="chevron-left" size={16} /> Back
			</a>
		{:else}
			<span></span>
		{/if}
		{#if data.step !== 'partner'}
			<a
				href={skipHref}
				class="btn min-h-11 rounded-full btn-ghost btn-sm"
				data-testid="setup-skip"
			>
				Skip for now <Icon name="chevron-right" size={16} />
			</a>
		{/if}
	</div>
</main>
