<script lang="ts">
	import '../app.css';
	import { PUBLIC_APP_NAME } from '$app/env/public';
	import Toasts from '#lib/components/Toasts.svelte';
	import type { LayoutProps } from './$types';

	let { children }: LayoutProps = $props();

	// Theme "system": link the manifest matching the phone's light/dark setting, so an installed app's Android navigation bar matches
	// the page (a chosen theme gets its manifest from the server).
	$effect(() => {
		if (document.documentElement.dataset.theme) return;
		const link = document.querySelector<HTMLLinkElement>('link[rel="manifest"]');
		if (!link) return;
		const media = matchMedia('(prefers-color-scheme: dark)');
		const apply = () => {
			link.href = `/manifest.webmanifest?scheme=${media.matches ? 'dark' : 'light'}`;
		};
		apply();
		media.addEventListener('change', apply);
		return () => media.removeEventListener('change', apply);
	});
</script>

<svelte:head>
	<title>{PUBLIC_APP_NAME}</title>
	<meta name="description" content="See your shared money at a glance." />
</svelte:head>

{@render children()}
<Toasts />
