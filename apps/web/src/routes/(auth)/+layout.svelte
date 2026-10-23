<script lang="ts">
	import { PUBLIC_APP_NAME } from '$app/env/public';
	import Icon from '#lib/components/Icon.svelte';
	import type { LayoutProps } from './$types';

	let { children }: LayoutProps = $props();
</script>

<main class="mx-auto flex min-h-dvh w-full max-w-md flex-col px-5 safe-top pb-10">
	<div class="flex flex-1 flex-col justify-center gap-8 py-10">
		<div class="flex flex-col items-center gap-3 text-center">
			<span
				class="flex h-16 w-16 items-center justify-center rounded-[1.25rem] bg-primary text-primary-content shadow-md"
			>
				<Icon name="heart" size={32} strokeWidth={2.4} />
			</span>
			<div>
				<h1 class="text-2xl font-bold tracking-tight">{PUBLIC_APP_NAME}</h1>
				<p class="text-sm text-base-content/60">Your money, together.</p>
			</div>
		</div>
		{@render children()}
	</div>
</main>
