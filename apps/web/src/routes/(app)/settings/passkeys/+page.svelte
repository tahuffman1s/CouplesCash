<script lang="ts">
	import { enhance } from '$app/forms';
	import { invalidateAll } from '$app/navigation';
	import type { Passkey } from '#lib/types.ts';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import { apiFetch } from '#lib/utils/client.ts';
	import { formatTimestamp } from '#lib/utils/dates.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data, form }: PageProps = $props();

	let supported = $state<boolean | null>(null);
	let adding = $state(false);
	let name = $state('');
	let working = $state(false);

	$effect(() => {
		(async () => {
			if (!window.isSecureContext || !('PublicKeyCredential' in window)) {
				supported = false;
				return;
			}
			const { browserSupportsWebAuthn } = await import('@simplewebauthn/browser');
			supported = browserSupportsWebAuthn();
		})();
	});

	function suggestName() {
		const ua = navigator.userAgent;
		const device = /iPhone/.test(ua)
			? 'iPhone'
			: /iPad/.test(ua)
				? 'iPad'
				: /Android/.test(ua)
					? 'Android phone'
					: /Mac/.test(ua)
						? 'Mac'
						: /Windows/.test(ua)
							? 'Windows PC'
							: 'device';
		const first = (data.user?.name ?? 'My').split(' ')[0];
		return `${first}'s ${device}`;
	}

	async function register(e: SubmitEvent) {
		e.preventDefault();
		const label = name.trim() || suggestName();
		if (working) return;
		working = true;
		try {
			const { startRegistration } = await import('@simplewebauthn/browser');
			const { challengeId, options } = await apiFetch<{
				challengeId: string;
				options: Parameters<typeof startRegistration>[0]['optionsJSON'];
			}>('/auth/passkeys/register/options', {
				method: 'POST',
				body: JSON.stringify({ name: label })
			});
			const credential = await startRegistration({ optionsJSON: options });
			await apiFetch<Passkey>('/auth/passkeys/register', {
				method: 'POST',
				body: JSON.stringify({ challengeId, name: label, credential })
			});
			toasts.show(`Passkey "${label}" added`, 'success');
			adding = false;
			name = '';
			await invalidateAll();
		} catch (err) {
			const n = (err as Error)?.name;
			if (n === 'NotAllowedError') toasts.show('Passkey setup was cancelled.', 'info');
			else if (n === 'InvalidStateError')
				toasts.show('This device already has a passkey for CouplesCash.', 'info');
			else toasts.show('Could not add a passkey.', 'error');
		} finally {
			working = false;
		}
	}

	$effect(() => {
		if (form?.deleted) toasts.show('Passkey removed', 'info');
		if (form?.error) toasts.show(form.error, 'error');
	});
</script>

<svelte:head><title>Passkeys · CouplesCash</title></svelte:head>

<AppBar title="Passkeys" back="/settings" />

<main class="flex flex-col gap-4 px-4 pt-2">
	{#if !data.config.enabled}
		<EmptyState
			icon="key-round"
			title="Passkeys aren't available here"
			body="Passkeys need HTTPS and a configured relying party on the server. Open CouplesCash through its public address to use them."
		>
			<p class="mt-2 text-xs text-base-content/50" data-testid="passkeys-disabled">
				Server reported: passkeys disabled.
			</p>
		</EmptyState>
	{:else if supported === false}
		<EmptyState
			icon="key-round"
			title="This browser can't use passkeys"
			body="Try Safari on iPhone, Chrome on Android, or a recent desktop browser over HTTPS."
		/>
	{:else}
		<section class="card border border-base-300/60 bg-base-100 shadow-sm">
			<div class="card-body gap-3 p-5">
				<div class="flex items-start gap-3">
					<span
						class="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-secondary/15 text-secondary"
					>
						<Icon name="fingerprint" size={22} />
					</span>
					<p class="text-sm text-base-content/70">
						Sign in with Face ID, Touch ID or your phone's unlock instead of a password. Each device
						gets its own passkey; your password keeps working as a fallback.
					</p>
				</div>
				{#if adding}
					<form class="flex flex-col gap-3" onsubmit={register}>
						<label class="form-control">
							<span class="label-text mb-1 text-sm font-medium">Name this passkey</span>
							<input
								type="text"
								maxlength="40"
								class="input-bordered input min-h-12 w-full"
								placeholder={suggestName()}
								bind:value={name}
							/>
						</label>
						<div class="flex gap-2">
							<button class="btn min-h-12 flex-1 rounded-full btn-primary" disabled={working}>
								{#if working}<span class="loading loading-sm loading-spinner"></span>{/if}
								Continue
							</button>
							<button
								type="button"
								class="btn min-h-12 rounded-full btn-ghost"
								onclick={() => (adding = false)}
							>
								Cancel
							</button>
						</div>
					</form>
				{:else}
					<button
						type="button"
						class="btn min-h-12 rounded-full btn-primary"
						onclick={() => (adding = true)}
						disabled={supported === null}
					>
						<Icon name="plus" size={18} strokeWidth={2.5} /> Add a passkey
					</button>
				{/if}
			</div>
		</section>

		<section class="card border border-base-300/60 bg-base-100 shadow-sm" aria-labelledby="list">
			<div class="card-body gap-2 p-5">
				<h2 id="list" class="font-semibold">Your passkeys</h2>
				{#if data.passkeys.length === 0}
					<p class="text-sm text-base-content/60">None yet.</p>
				{:else}
					<ul class="divide-y divide-base-300/60 text-sm">
						{#each data.passkeys as pk (pk.id)}
							<li class="flex items-center gap-3 py-2">
								<Icon name="key-round" size={18} class="shrink-0 text-base-content/50" />
								<div class="min-w-0 flex-1">
									<p class="truncate font-medium">{pk.name}</p>
									<p class="text-xs text-base-content/50">
										Added {formatTimestamp(pk.createdAt)}{pk.lastUsedAt
											? ` · last used ${formatTimestamp(pk.lastUsedAt)}`
											: ''}{pk.backedUp ? ' · synced' : ''}
									</p>
								</div>
								<form method="POST" action="?/delete" use:enhance>
									<input type="hidden" name="id" value={pk.id} />
									<button
										class="btn btn-circle min-h-11 min-w-11 btn-ghost btn-sm"
										aria-label="Remove passkey {pk.name}"
									>
										<Icon name="trash" size={16} />
									</button>
								</form>
							</li>
						{/each}
					</ul>
				{/if}
			</div>
		</section>
	{/if}
</main>
