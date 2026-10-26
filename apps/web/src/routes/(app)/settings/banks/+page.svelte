<script lang="ts">
	import { enhance } from '$app/forms';
	import type { BankLink } from '#lib/types.ts';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import BottomSheet from '#lib/components/BottomSheet.svelte';
	import BankLinkCard from '#lib/components/BankLinkCard.svelte';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data, form }: PageProps = $props();

	let submitting = $state(false);
	let unlinking = $state<BankLink | null>(null);
	let sheet = $state(false);
	const notConfigured = $derived(data.notConfigured || !!form?.notConfigured);
	const links = $derived(data.links ?? []);

	$effect(() => {
		if (form?.linked)
			toasts.show(
				`Linked ${form.linked} · ${form.accounts} account${form.accounts === 1 ? '' : 's'} found`,
				'success'
			);
		if (form?.renamed) toasts.show('Link renamed', 'success');
		if (form?.toggled) toasts.show(`Syncing ${form.toggled}`, 'info');
		if (form?.unlinked) {
			toasts.show('Bank unlinked', 'info');
			sheet = false;
		}
		if (form?.error && !form?.values) toasts.show(form.error, 'error');
	});

	function askUnlink(link: BankLink) {
		unlinking = link;
		sheet = true;
	}
</script>

<svelte:head><title>Linked banks · CouplesCash</title></svelte:head>

<AppBar title="Linked banks" back="/settings#banks" />

<main class="flex flex-col gap-4 px-4 pt-2">
	<section class="card border border-base-300/60 bg-base-100 shadow-sm" aria-labelledby="how">
		<div class="card-body gap-3 p-5">
			<h2 id="how" class="flex items-center gap-2 font-semibold">
				<Icon name="landmark" size={18} /> How bank sync works
			</h2>
			<p class="text-sm text-base-content/70">
				CouplesCash reads balances and transactions through <strong>SimpleFIN Bridge</strong>, a
				read-only service built for self-hosters. It costs <strong>$1.50 a month</strong>, never
				sees your CouplesCash login, and can't move money. Every pulled transaction lands in the
				<a href="/transactions" class="link link-primary">To review</a> for you to confirm.
			</p>
			<ol class="list-inside list-decimal space-y-1 text-sm text-base-content/80">
				<li>
					Connect your banks at
					<a
						href="https://beta-bridge.simplefin.org/simplefin/create"
						target="_blank"
						rel="noopener noreferrer"
						class="link link-primary">beta-bridge.simplefin.org</a
					>
					<Icon
						name="external-link"
						size={12}
						class="inline align-baseline"
						label="opens in a new tab"
					/>
				</li>
				<li>Choose <strong>Setup Token</strong> and copy the long code it shows.</li>
				<li>Paste it below. The token works once and expires quickly, so do it right away.</li>
			</ol>
		</div>
	</section>

	{#if notConfigured}
		<div
			class="alert items-start text-sm alert-warning"
			role="status"
			data-testid="banks-not-configured"
		>
			<Icon name="triangle-alert" size={18} />
			<span>
				Bank sync isn't configured on this server (<code>DATA_KEY</code> missing). Whoever runs
				CouplesCash can turn it on by following <code>docs/BANK-SYNC.md</code>; this page lights up
				afterwards.
			</span>
		</div>
	{:else}
		<section class="card border border-base-300/60 bg-base-100 shadow-sm" aria-labelledby="link">
			<form
				method="POST"
				action="?/claim"
				class="card-body gap-3 p-5"
				use:enhance={() => {
					submitting = true;
					return async ({ update }) => {
						await update();
						submitting = false;
					};
				}}
			>
				<h2 id="link" class="font-semibold">Link a bank</h2>
				{#if form?.error && form?.values}
					<div class="alert text-sm alert-error" role="alert">{form.error}</div>
				{/if}
				<label class="form-control">
					<span class="label-text mb-1 text-sm font-medium">Name</span>
					<input
						name="name"
						type="text"
						maxlength="40"
						class="input-bordered input min-h-12 w-full"
						placeholder="My bank"
						defaultValue={form?.values?.name ?? 'My bank'}
					/>
				</label>
				<label class="form-control">
					<span class="label-text mb-1 text-sm font-medium">Setup token</span>
					<textarea
						name="setupToken"
						required
						rows="3"
						class="textarea-bordered textarea w-full font-mono text-xs"
						placeholder="aHR0cHM6Ly9iZXRhLWJyaWRnZS5zaW1wbGVmaW4ub3JnL3NpbXBsZWZpbi9jbGFpbS8…"
						autocomplete="off"
						autocapitalize="off"
						spellcheck="false"
						defaultValue={form?.values?.setupToken ?? ''}></textarea>
					<span class="label-text-alt mt-1 text-base-content/60"
						>Stored encrypted on your server; never shown again.</span
					>
				</label>
				<button class="btn min-h-12 rounded-full btn-primary" disabled={submitting}>
					{#if submitting}<span class="loading loading-sm loading-spinner"></span>{/if}
					Link bank
				</button>
			</form>
		</section>

		{#each links as link (link.id)}
			<BankLinkCard {link} accounts={data.accounts} onUnlink={askUnlink} />
		{/each}
	{/if}
</main>

<BottomSheet bind:open={sheet} title="Unlink {unlinking?.name ?? 'bank'}?">
	{#if unlinking}
		<p class="text-sm text-base-content/70">
			Syncing stops and the accounts from this link disappear from this page. Transactions already
			in Transactions and the CouplesCash accounts you mapped to are kept.
		</p>
		<form method="POST" action="?/unlink" class="mt-4 flex gap-2" use:enhance>
			<input type="hidden" name="id" value={unlinking.id} />
			<button class="btn min-h-12 flex-1 rounded-full btn-error">Unlink</button>
			<button
				type="button"
				class="btn min-h-12 rounded-full btn-ghost"
				onclick={() => (sheet = false)}>Keep</button
			>
		</form>
	{/if}
</BottomSheet>
