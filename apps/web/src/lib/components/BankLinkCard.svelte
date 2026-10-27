<script lang="ts">
	import { enhance } from '$app/forms';
	import { invalidateAll } from '$app/navigation';
	import type { Account, BankLink, ExternalAccount, SyncResult } from '#lib/types.ts';
	import { apiFetch, ClientApiError } from '#lib/utils/client.ts';
	import { relativeTime } from '#lib/utils/dates.ts';
	import { announcePlaid, openPlaid } from '#lib/utils/plaid.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';
	import Icon from './Icon.svelte';
	import ExternalAccountRow from './ExternalAccountRow.svelte';

	let {
		link,
		accounts,
		onUnlink
	}: {
		link: BankLink;
		accounts: Account[];
		onUnlink: (link: BankLink) => void;
	} = $props();

	let renaming = $state(false);
	let syncing = $state(false);
	let reconnecting = $state(false);
	const plaid = $derived(link.provider === 'plaid');
	// local copy so a mapping change shows immediately; reset when the server data changes
	let exts = $derived<ExternalAccount[]>(link.accounts);

	const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`;

	async function syncNow() {
		if (syncing) return;
		syncing = true;
		try {
			const r = await apiFetch<SyncResult>(`/banks/${link.id}/sync`, { method: 'POST' });
			toasts.show(
				`${plural(r.newTransactions, 'new transaction')}, ${plural(r.updatedAccounts, 'balance')} updated`,
				'success'
			);
			if (r.errors.length) toasts.show(r.errors[0], 'error', 6000);
			void invalidateAll();
		} catch (e) {
			if (e instanceof ClientApiError && e.status === 429) {
				const mins = e.retryAfterSeconds ? Math.max(1, Math.ceil(e.retryAfterSeconds / 60)) : null;
				toasts.show(
					mins ? `Synced recently, try again in ${mins} min` : 'Synced recently, try again soon',
					'info'
				);
			} else if (e instanceof ClientApiError && e.status === 502) {
				toasts.show(
					e.problem.detail ||
						(plaid
							? 'Plaid is not reachable right now.'
							: 'SimpleFIN Bridge is not reachable right now.'),
					'error'
				);
			} else {
				toasts.show('Could not sync. Check your connection.', 'error');
			}
		} finally {
			syncing = false;
		}
	}

	/** Plaid Link in update mode: sign in to the bank again, then the API syncs this link. */
	async function reconnect() {
		if (reconnecting) return;
		reconnecting = true;
		try {
			const outcome = await openPlaid(link.id);
			announcePlaid(outcome);
			if (outcome.kind !== 'exit') await invalidateAll();
		} catch (e) {
			toasts.show(
				e instanceof ClientApiError
					? e.problem.detail || 'Plaid is not reachable right now.'
					: 'Could not open Plaid.',
				'error'
			);
		} finally {
			reconnecting = false;
		}
	}

	function onMapped(next: ExternalAccount, created: boolean) {
		exts = exts.map((x) => (x.id === next.id ? next : x));
		// a created account (and the mapped account's new balance) show up on invalidation
		if (created) void invalidateAll();
	}
</script>

<section
	class="card border border-base-300/60 bg-base-100 shadow-sm"
	aria-labelledby="link-{link.id}"
	data-testid="bank-link"
>
	<div class="card-body gap-3 p-5">
		<div class="flex items-start gap-3">
			<span
				class="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-primary/10 text-primary"
				aria-hidden="true"
			>
				<Icon name="landmark" size={20} />
			</span>
			<div class="min-w-0 flex-1">
				{#if renaming}
					<form
						method="POST"
						action="?/rename"
						class="flex gap-2"
						use:enhance={() =>
							async ({ update }) => {
								await update();
								renaming = false;
							}}
					>
						<input type="hidden" name="id" value={link.id} />
						<input
							name="name"
							type="text"
							required
							maxlength="40"
							class="input-bordered input min-h-11 flex-1 input-sm"
							aria-label="Link name"
							defaultValue={link.name}
						/>
						<button class="btn min-h-11 rounded-xl btn-secondary btn-sm">Save</button>
					</form>
				{:else}
					<h2 id="link-{link.id}" class="flex items-center gap-1 font-semibold">
						<span class="truncate">{link.name}</span>
						<button
							type="button"
							class="btn btn-circle min-h-9 min-w-9 btn-ghost btn-xs"
							aria-label="Rename {link.name}"
							onclick={() => (renaming = true)}
						>
							<Icon name="pencil" size={14} />
						</button>
					</h2>
				{/if}
				<p class="text-xs text-base-content/60">
					{#if link.lastSyncedAt}
						Synced {relativeTime(link.lastSyncedAt)}
					{:else}
						Not synced yet
					{/if}
					<span aria-hidden="true"> · </span>via {plaid ? 'Plaid' : 'SimpleFIN'}
					{#if !link.syncEnabled}<span aria-hidden="true"> · </span>Paused{/if}
				</p>
			</div>
			<button
				type="button"
				class="btn min-h-11 rounded-full border border-base-300 btn-ghost px-3 btn-sm"
				disabled={syncing}
				onclick={syncNow}
			>
				{#if syncing}
					<span class="loading loading-xs loading-spinner"></span>
				{:else}
					<Icon name="refresh-cw" size={16} />
				{/if}
				Sync now
			</button>
		</div>

		{#if link.lastError}
			<div class="alert items-start text-sm alert-warning" role="alert">
				<Icon name="triangle-alert" size={18} />
				<span>{link.lastError}</span>
			</div>
		{/if}
		{#if plaid && link.needsReconnect}
			<button
				type="button"
				class="btn min-h-12 rounded-full btn-primary"
				disabled={reconnecting}
				onclick={reconnect}
				data-testid="plaid-reconnect"
			>
				{#if reconnecting}<span class="loading loading-sm loading-spinner"></span>{/if}
				Reconnect {link.name}
			</button>
		{/if}
		{#each link.bridgeErrors as err, i (i)}
			<div class="alert items-start text-sm alert-warning" role="alert">
				<Icon name="circle-alert" size={18} />
				<span>{err}</span>
			</div>
		{/each}

		{#if exts.length === 0}
			<p class="text-sm text-base-content/60">
				{#if plaid}
					No accounts came through yet. Plaid can take a few minutes after connecting; this page
					fills in on the next sync.
				{:else}
					No accounts came through yet. Sync again in a moment, or check the Bridge for connection
					problems.
				{/if}
			</p>
		{:else}
			<ul class="divide-y divide-base-300/60" aria-label="Accounts from {link.name}">
				{#each exts as ext (ext.id)}
					<ExternalAccountRow {ext} {accounts} {onMapped} />
				{/each}
			</ul>
		{/if}

		<div class="flex items-center justify-between gap-2 border-t border-base-300/60 pt-3">
			<form method="POST" action="?/toggle" use:enhance>
				<input type="hidden" name="id" value={link.id} />
				<label class="label cursor-pointer gap-3 px-0">
					<input
						type="checkbox"
						name="syncEnabled"
						class="toggle toggle-secondary toggle-sm"
						checked={link.syncEnabled}
						onchange={(e) => e.currentTarget.form?.requestSubmit()}
					/>
					<span class="label-text text-sm"
						>{link.syncEnabled
							? plaid
								? 'Syncing every hour'
								: 'Syncing every few hours'
							: 'Paused'}</span
					>
				</label>
			</form>
			<button
				type="button"
				class="btn min-h-11 rounded-full btn-ghost px-3 text-error btn-sm"
				onclick={() => onUnlink(link)}
			>
				<Icon name="unlink" size={16} /> Unlink
			</button>
		</div>
	</div>
</section>
