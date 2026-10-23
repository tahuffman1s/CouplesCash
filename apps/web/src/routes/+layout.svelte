<script lang="ts">
	import '../app.css';
	import { PUBLIC_APP_NAME } from '$app/env/public';
	import Toasts from '#lib/components/Toasts.svelte';
	import type { LayoutProps } from './$types';

	let { children }: LayoutProps = $props();
</script>

<svelte:head>
	<title>{PUBLIC_APP_NAME}</title>
	<meta name="description" content="See your shared money at a glance." />
</svelte:head>

{@render children()}
<Toasts />
