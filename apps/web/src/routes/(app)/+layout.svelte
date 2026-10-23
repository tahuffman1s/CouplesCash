<script lang="ts">
	import Dock from '#lib/components/Dock.svelte';
	import type { LayoutProps } from './$types';

	let { children }: LayoutProps = $props();
</script>

<div class="mx-auto flex min-h-dvh w-full max-w-xl flex-col pb-dock">
	{@render children()}
</div>
<Dock />
