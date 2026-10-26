<script lang="ts">
	import { enhance } from '$app/forms';
	import { page } from '$app/state';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import CoupleCodeCard from '#lib/components/CoupleCodeCard.svelte';
	import CodeScanner from '#lib/components/CodeScanner.svelte';
	import { formatDate } from '#lib/utils/dates.ts';
	import { normaliseCoupleCode } from '#lib/utils/couple-code.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data, form }: PageProps = $props();

	type Mode = 'menu' | 'show' | 'scan' | 'type';
	let mode = $state<Mode>('menu');
	let typed = $state('');
	let joinForm = $state<HTMLFormElement | null>(null);
	let confirmLeave = $state(false);
	let generating = $state(false);

	const household = $derived(data.household);
	const linked = $derived(!!household && household.members.length >= 2);
	const me = $derived(household?.members.find((m) => m.id === data.user?.id));
	const partner = $derived(household?.members.find((m) => m.id !== data.user?.id));

	$effect(() => {
		if (form?.code) mode = 'show';
		if (form?.joinError) mode = 'type';
	});
	$effect(() => {
		if (page.url.searchParams.has('joined')) toasts.show('You are linked! 🎉', 'success');
	});

	function onScanned(text: string) {
		const code = normaliseCoupleCode(text);
		if (!code) {
			toasts.show("That QR isn't a Couple Code.", 'error');
			mode = 'type';
			return;
		}
		typed = code;
		mode = 'type';
		queueMicrotask(() => joinForm?.requestSubmit());
	}
</script>

<svelte:head><title>Couple · CouplesCash</title></svelte:head>

<AppBar title="Couple" back={mode === 'menu' ? '/settings#household' : undefined}>
	{#snippet action()}
		{#if mode !== 'menu'}
			<button type="button" class="btn min-h-11 btn-ghost btn-sm" onclick={() => (mode = 'menu')}
				>Done</button
			>
		{/if}
	{/snippet}
</AppBar>

<main class="flex flex-col gap-4 px-4 pt-2">
	{#if form?.error}
		<div class="alert text-sm alert-error" role="alert">{form.error}</div>
	{/if}

	{#if linked && household}
		<!-- Linked state -->
		<section class="card border border-base-300/60 bg-base-100 shadow-sm">
			<div class="card-body items-center gap-4 p-6 text-center">
				<div class="flex items-center -space-x-3">
					{#each [me, partner] as m, i (m?.id ?? i)}
						<div class="avatar avatar-placeholder">
							<div
								class="{i === 0
									? 'bg-primary text-primary-content'
									: 'bg-secondary text-secondary-content'} w-16 rounded-full ring-4 ring-base-100"
							>
								<span class="text-2xl font-semibold"
									>{(m?.displayName ?? '?').slice(0, 1).toUpperCase()}</span
								>
							</div>
						</div>
					{/each}
				</div>
				<div>
					<h2 class="text-xl font-bold">{household.name}</h2>
					<p class="text-sm text-base-content/65">
						{me?.displayName ?? 'You'}
						<Icon name="heart" size={14} class="inline align-middle text-primary" label="and" />
						{partner?.displayName ?? 'your partner'}
					</p>
					<p class="mt-1 text-xs text-base-content/50">
						Linked since {formatDate((partner?.joinedAt ?? household.createdAt).slice(0, 10))}
					</p>
				</div>
			</div>
		</section>

		<section class="card border border-base-300/60 bg-base-100 shadow-sm">
			<form method="POST" action="?/rename" class="card-body gap-2 p-5" use:enhance>
				<label class="form-control">
					<span class="label-text mb-1 text-sm font-medium">Household name</span>
					<div class="flex gap-2">
						<input
							name="name"
							type="text"
							required
							maxlength="40"
							class="input-bordered input min-h-12 flex-1"
							defaultValue={household.name}
						/>
						<button class="btn min-h-12 rounded-xl btn-secondary">Save</button>
					</div>
				</label>
			</form>
		</section>

		<form
			method="POST"
			action="?/leave"
			use:enhance
			class="card border border-error/30 bg-base-100"
		>
			<div class="card-body gap-2 p-5">
				<h2 class="font-semibold">Leave household</h2>
				<p class="text-sm text-base-content/65">
					Your partner keeps the accounts and bills. You start fresh.
				</p>
				{#if confirmLeave}
					<div class="flex gap-2">
						<button class="btn min-h-11 flex-1 rounded-full btn-error">Yes, leave</button>
						<button
							type="button"
							class="btn min-h-11 flex-1 rounded-full btn-ghost"
							onclick={() => (confirmLeave = false)}>Cancel</button
						>
					</div>
				{:else}
					<button
						type="button"
						class="btn min-h-11 rounded-full btn-outline btn-error"
						onclick={() => (confirmLeave = true)}>Leave</button
					>
				{/if}
			</div>
		</form>
	{:else if mode === 'menu'}
		<!-- Unlinked: choose a path -->
		<section
			class="card border border-base-300/60 bg-gradient-to-br from-primary/15 to-secondary/15 shadow-sm"
		>
			<div class="card-body items-center gap-2 p-6 text-center">
				<span
					class="flex h-16 w-16 items-center justify-center rounded-full bg-base-100 text-primary shadow-sm"
				>
					<Icon name="heart-handshake" size={32} />
				</span>
				<h2 class="text-xl font-bold">
					{household ? `Invite a partner to ${household.name}` : 'Link up with your partner'}
				</h2>
				<p class="text-sm text-base-content/65">
					{household
						? 'Show them your Couple Code, or they can type it. Codes last 10 minutes and work once.'
						: 'One of you creates the household and shows a Couple Code; the other scans or types it.'}
				</p>
				{#if household}
					<p class="text-xs text-base-content/55" data-testid="invite-new-partner">
						If a partner has left, a fresh Couple Code invites a new one – everything in {household.name}
						stays as it is.
					</p>
				{/if}
			</div>
		</section>

		<form
			method="POST"
			action="?/code"
			class="card border border-base-300/60 bg-base-100 shadow-sm"
			use:enhance={() => {
				generating = true;
				return async ({ update }) => {
					await update({ reset: false });
					generating = false;
				};
			}}
		>
			<div class="card-body gap-3 p-5">
				{#if !household}
					<label class="form-control">
						<span class="label-text mb-1 text-sm font-medium">Name your household</span>
						<input
							name="name"
							type="text"
							maxlength="40"
							class="input-bordered input min-h-12 w-full"
							placeholder="The Riveras"
						/>
					</label>
				{/if}
				<button class="btn min-h-12 rounded-full btn-primary" disabled={generating}>
					{#if generating}<span class="loading loading-sm loading-spinner"></span>{/if}
					<Icon name="qr-code" size={18} />
					{household ? 'Show my Couple Code' : 'Create household & show code'}
				</button>
			</div>
		</form>

		{#if !household}
			<div class="grid grid-cols-2 gap-3">
				<button
					type="button"
					class="card min-h-24 border border-base-300/60 bg-base-100 shadow-sm"
					onclick={() => (mode = 'scan')}
				>
					<div class="card-body items-center gap-2 p-4 text-center">
						<Icon name="camera" size={24} class="text-secondary" />
						<span class="font-medium">Scan partner's code</span>
					</div>
				</button>
				<button
					type="button"
					class="card min-h-24 border border-base-300/60 bg-base-100 shadow-sm"
					onclick={() => (mode = 'type')}
				>
					<div class="card-body items-center gap-2 p-4 text-center">
						<Icon name="keyboard" size={24} class="text-secondary" />
						<span class="font-medium">Type a code</span>
					</div>
				</button>
			</div>
		{:else}
			<p class="text-center text-sm text-base-content/60">
				Waiting for your partner to join. You can start adding accounts and bills in the meantime.
			</p>
		{/if}
	{:else if mode === 'show' && form?.code}
		<section class="card border border-base-300/60 bg-base-100 shadow-sm">
			<div class="card-body gap-4 p-5">
				<CoupleCodeCard code={form.code} />
				<form method="POST" action="?/code" use:enhance class="flex justify-center">
					<button class="btn min-h-11 rounded-full border border-base-300 btn-ghost">
						<Icon name="refresh-cw" size={18} /> Generate new code
					</button>
				</form>
			</div>
		</section>
	{:else if mode === 'scan'}
		<section class="card border border-base-300/60 bg-base-100 shadow-sm">
			<div class="card-body gap-4 p-5">
				<CodeScanner onResult={onScanned} />
				<button
					type="button"
					class="btn min-h-11 rounded-full btn-ghost"
					onclick={() => (mode = 'type')}
				>
					<Icon name="keyboard" size={18} /> Type the code instead
				</button>
			</div>
		</section>
	{:else}
		<!-- type -->
		<form
			method="POST"
			action="?/join"
			bind:this={joinForm}
			class="card border border-base-300/60 bg-base-100 shadow-sm"
			use:enhance
		>
			<div class="card-body gap-3 p-5">
				<label class="form-control">
					<span class="label-text mb-1 text-sm font-medium">Partner's Couple Code</span>
					<input
						name="code"
						type="text"
						autocomplete="off"
						autocapitalize="characters"
						spellcheck="false"
						required
						class="input-bordered input min-h-14 w-full text-center text-2xl tracking-[0.2em] uppercase tabular"
						placeholder="ABCD-EFGH"
						bind:value={typed}
					/>
				</label>
				{#if form?.joinError}<p class="text-sm text-error" role="alert">{form.joinError}</p>{/if}
				<button class="btn min-h-12 rounded-full btn-primary">
					<Icon name="link" size={18} /> Join household
				</button>
				<button
					type="button"
					class="btn min-h-11 rounded-full btn-ghost"
					onclick={() => (mode = 'scan')}
				>
					<Icon name="camera" size={18} /> Scan instead
				</button>
			</div>
		</form>
	{/if}
</main>
