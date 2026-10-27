<script lang="ts">
	import { onMount } from 'svelte';
	import { goto } from '$app/navigation';
	import AppBar from '#lib/components/AppBar.svelte';
	import { announcePlaid, resumePlaid } from '#lib/utils/plaid.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	/**
	 * OAuth banks send Plaid Link to the bank's own site; on a phone that leaves this page, and the bank sends you back
	 * here (Plaid__RedirectUri). Link re-opens with this URL and finishes the connection, then it's back to Linked banks.
	 */
	let message = $state('Finishing the connection with your bank…');

	onMount(() => {
		const pending = resumePlaid(window.location.href);
		if (!pending) {
			void goto('/settings/banks', { replaceState: true });
			return;
		}
		pending
			.then((outcome) => {
				announcePlaid(outcome);
				return goto('/settings/banks', { replaceState: true, invalidateAll: true });
			})
			.catch((e: unknown) => {
				message = 'Could not finish connecting. Try again from Linked banks.';
				toasts.show(e instanceof Error ? e.message : message, 'error', 6000);
			});
	});
</script>

<svelte:head><title>Connecting your bank · CouplesCash</title></svelte:head>

<AppBar title="Connecting your bank" back="/settings/banks" />

<main class="flex flex-col items-center gap-3 px-4 pt-10 text-center" data-testid="plaid-oauth">
	<span class="loading loading-lg loading-spinner text-primary"></span>
	<p class="text-sm text-base-content/70">{message}</p>
</main>
