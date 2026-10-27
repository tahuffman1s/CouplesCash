<script lang="ts">
	import { invalidateAll } from '$app/navigation';
	import type { PlaidStatus } from '#lib/types.ts';
	import { ClientApiError } from '#lib/utils/client.ts';
	import { announcePlaid, openPlaid } from '#lib/utils/plaid.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';
	import Icon from './Icon.svelte';

	let { status }: { status: PlaidStatus } = $props();

	let busy = $state(false);

	async function connect() {
		if (busy) return;
		busy = true;
		try {
			const outcome = await openPlaid();
			announcePlaid(outcome);
			if (outcome.kind !== 'exit') await invalidateAll();
		} catch (e) {
			toasts.show(
				e instanceof ClientApiError
					? e.problem.detail || 'Plaid is not reachable right now.'
					: e instanceof Error
						? e.message
						: 'Could not open Plaid.',
				'error',
				6000
			);
		} finally {
			busy = false;
		}
	}
</script>

<section
	class="card border border-primary/30 bg-base-100 shadow-sm"
	aria-labelledby="plaid"
	data-testid="plaid-card"
>
	<div class="card-body gap-3 p-5">
		<h2 id="plaid" class="flex items-center gap-2 font-semibold">
			<Icon name="landmark" size={18} /> Connect with Plaid
			{#if status.environment === 'sandbox'}
				<span class="badge badge-sm badge-warning">sandbox</span>
			{/if}
		</h2>
		<p class="text-sm text-base-content/70">
			Sign in to your bank in Plaid's window; CouplesCash only ever gets read-only access. Plaid
			checks most banks several times a day, and <strong>Sync now</strong> asks it to check right away.
		</p>
		<button
			type="button"
			class="btn min-h-12 rounded-full btn-primary"
			disabled={busy}
			onclick={connect}
			data-testid="plaid-connect"
		>
			{#if busy}<span class="loading loading-sm loading-spinner"></span>{/if}
			Connect a bank
		</button>
	</div>
</section>
