<script lang="ts">
	import { enhance } from '$app/forms';
	import { invalidateAll } from '$app/navigation';
	import type { Account, BalanceSnapshot } from '#lib/types.ts';
	import { LIABILITY_TYPES } from '#lib/types.ts';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import AccountForm from '#lib/components/AccountForm.svelte';
	import Sparkline from '#lib/charts/Sparkline.svelte';
	import { ACCOUNT_TYPE_LABEL } from '#lib/utils/labels.ts';
	import { centsToInput, formatMoney, parseMoney } from '#lib/utils/money.ts';
	import { formatTimestamp } from '#lib/utils/dates.ts';
	import { apiFetch } from '#lib/utils/client.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data, form }: PageProps = $props();

	let account = $derived<Account>(data.account);
	let history = $derived<BalanceSnapshot[]>(data.history);

	let editing = $state(false);
	let submitting = $state(false);
	let quick = $state('');
	let quickBusy = $state(false);
	let confirmDelete = $state(false);

	const liability = $derived(LIABILITY_TYPES.has(account.type));
	const spark = $derived(
		[...history].reverse().map((h) => ({ at: h.recordedAt, value: h.balanceCents }))
	);

	async function quickUpdate(e: SubmitEvent) {
		e.preventDefault();
		const cents = parseMoney(quick);
		if (cents === null) {
			toasts.show('Enter a number like 1250.00', 'error');
			return;
		}
		const previous = account;
		account = { ...account, balanceCents: cents };
		quickBusy = true;
		try {
			account = await apiFetch<Account>(`/accounts/${account.id}/balance`, {
				method: 'POST',
				body: JSON.stringify({ balanceCents: cents })
			});
			history = [
				{
					balanceCents: cents,
					recordedAt: new Date().toISOString(),
					recordedBy: data.user?.id ?? ''
				},
				...history
			];
			quick = '';
			toasts.show('Balance updated', 'success');
			void invalidateAll();
		} catch {
			account = previous;
			toasts.show('Could not update the balance.', 'error');
		} finally {
			quickBusy = false;
		}
	}

	$effect(() => {
		if (form?.saved) {
			editing = false;
			toasts.show('Account saved', 'success');
		}
	});
	$effect(() => {
		if (data.created) toasts.show('Account added', 'success');
	});
</script>

<svelte:head><title>{account.name} · CouplesCash</title></svelte:head>

<AppBar
	title={account.name}
	subtitle="{account.institution} · {ACCOUNT_TYPE_LABEL[account.type]}"
	back="/accounts"
>
	{#snippet action()}
		<button
			type="button"
			class="btn btn-circle min-h-11 min-w-11 btn-ghost btn-sm"
			aria-label={editing ? 'Stop editing' : 'Edit account'}
			aria-pressed={editing}
			onclick={() => (editing = !editing)}
		>
			<Icon name={editing ? 'x' : 'pencil'} size={20} />
		</button>
	{/snippet}
</AppBar>

<main class="flex flex-col gap-4 px-4 pt-2">
	{#if !editing}
		<section class="card border border-base-300/60 bg-base-100 shadow-sm">
			<div class="card-body gap-4 p-5">
				<div class="flex items-center gap-3">
					<span
						class="flex h-12 w-12 items-center justify-center rounded-2xl text-white"
						style:background={account.color}
					>
						<Icon name={account.icon} size={24} />
					</span>
					<div>
						<p class="text-xs text-base-content/60">{liability ? 'Amount owed' : 'Balance'}</p>
						<p
							class="text-3xl font-bold tracking-tight tabular {liability ? 'text-error' : ''}"
							data-testid="balance"
						>
							{formatMoney(account.balanceCents)}
						</p>
					</div>
				</div>

				<form class="flex gap-2" onsubmit={quickUpdate} aria-label="Quick balance update">
					<label class="input-bordered input flex min-h-12 flex-1 items-center gap-2">
						<span class="text-base-content/50">$</span>
						<input
							type="text"
							inputmode="decimal"
							class="grow tabular"
							placeholder={centsToInput(account.balanceCents)}
							aria-label="New balance"
							bind:value={quick}
						/>
					</label>
					<button class="btn min-h-12 rounded-xl btn-secondary" disabled={quickBusy || !quick}>
						{#if quickBusy}<span class="loading loading-sm loading-spinner"></span>{/if}
						Update
					</button>
				</form>

				{#if account.note}<p class="text-sm text-base-content/70">{account.note}</p>{/if}
			</div>
		</section>

		<section class="card border border-base-300/60 bg-base-100 shadow-sm" aria-labelledby="history">
			<div class="card-body gap-3 p-5">
				<h2 id="history" class="font-semibold">Balance history</h2>
				<Sparkline points={spark} color={account.color} />
				{#if history.length}
					<ul class="divide-y divide-base-300/60 text-sm">
						{#each history.slice(0, 8) as h, i (h.recordedAt + i)}
							<li class="flex justify-between py-2">
								<span class="text-base-content/60">{formatTimestamp(h.recordedAt)}</span>
								<span class="font-medium tabular">{formatMoney(h.balanceCents)}</span>
							</li>
						{/each}
					</ul>
				{/if}
			</div>
		</section>
	{:else}
		<form
			method="POST"
			action="?/update"
			class="card border border-base-300/60 bg-base-100 shadow-sm"
			use:enhance={() => {
				submitting = true;
				return async ({ update }) => {
					await update({ reset: false });
					submitting = false;
				};
			}}
		>
			<div class="card-body gap-4 p-5">
				<AccountForm {account} values={form?.values} error={form?.error} />
				<button class="btn min-h-12 rounded-full btn-primary" disabled={submitting}>
					{#if submitting}<span class="loading loading-sm loading-spinner"></span>{/if}
					Save changes
				</button>
			</div>
		</form>

		<form
			method="POST"
			action="?/delete"
			use:enhance
			class="card border border-error/30 bg-base-100"
		>
			<div class="card-body gap-2 p-5">
				<h2 class="font-semibold">Remove account</h2>
				<p class="text-sm text-base-content/65">
					Hides this account and its history from both of you. Bills linked to it stay.
				</p>
				{#if confirmDelete}
					<div class="flex gap-2">
						<button class="btn min-h-11 flex-1 rounded-full btn-error">Yes, remove</button>
						<button
							type="button"
							class="btn min-h-11 flex-1 rounded-full btn-ghost"
							onclick={() => (confirmDelete = false)}
						>
							Cancel
						</button>
					</div>
				{:else}
					<button
						type="button"
						class="btn min-h-11 rounded-full btn-outline btn-error"
						onclick={() => (confirmDelete = true)}
					>
						<Icon name="trash" size={18} /> Remove account
					</button>
				{/if}
			</div>
		</form>
	{/if}
</main>
