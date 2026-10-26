<script lang="ts">
	import { onMount, untrack } from 'svelte';
	import Dock from '#lib/components/Dock.svelte';
	import { currency } from '#lib/state/currency.svelte.ts';
	import { review } from '#lib/state/review.svelte.ts';
	import { apiFetch } from '#lib/utils/client.ts';
	import type { LayoutProps } from './$types';

	let { data, children }: LayoutProps = $props();

	function detectTimezone(): string {
		try {
			return Intl.DateTimeFormat().resolvedOptions().timeZone ?? '';
		} catch {
			return '';
		}
	}

	// Set during render (also on the server) so every money render below uses the household's
	// currency on the first paint.
	untrack(() => currency.set(data.household?.currency));
	$effect(() => currency.set(data.household?.currency));
	// the Transactions badge: seeded from the server on every navigation, nudged by review taps
	untrack(() => review.set(data.inbox?.pending));
	$effect(() => review.set(data.inbox?.pending));

	// Marks the app as interactive (e2e waits on it instead of sleeping)
	onMount(() => {
		document.documentElement.dataset.ready = '1';
	});

	// Time zone: auto-detected from the browser and posted once when the server still has the
	// default (UTC) and the device disagrees. Guarded per session so a failing PATCH never loops.
	$effect(() => {
		const hh = data.household;
		if (!hh || hh.timezone !== 'UTC') return;
		const tz = detectTimezone();
		if (!tz || tz === 'UTC' || tz === 'Etc/UTC') return;
		const key = `cc-tz-posted:${hh.id}`;
		try {
			if (sessionStorage.getItem(key)) return;
			sessionStorage.setItem(key, tz);
		} catch {
			// private mode – still try once per page load
		}
		void apiFetch('/couple', { method: 'PATCH', body: JSON.stringify({ timezone: tz }) }).catch(
			() => {}
		);
	});
</script>

<div class="mx-auto flex min-h-dvh w-full max-w-xl flex-col pb-dock">
	{@render children()}
</div>
<Dock />
