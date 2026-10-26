<script lang="ts">
	import type { Snippet } from 'svelte';
	import Icon from './Icon.svelte';

	let {
		title,
		back,
		subtitle,
		action,
		gear = !back
	}: {
		title: string;
		/** href for a back chevron on the left */
		back?: string;
		subtitle?: string;
		/** right-aligned context action */
		action?: Snippet;
		/** v7: the gear → Settings (on the three tab screens) */
		gear?: boolean;
	} = $props();
</script>

<header
	class="sticky top-0 z-20 bg-base-200/85 pt-[env(safe-area-inset-top)] backdrop-blur-md supports-[backdrop-filter]:bg-base-200/70 print:hidden"
>
	<div class="mx-auto flex h-14 w-full max-w-xl items-center gap-1 px-3">
		{#if back}
			<a href={back} class="btn btn-circle min-h-11 min-w-11 btn-ghost btn-sm" aria-label="Back">
				<Icon name="chevron-left" size={22} />
			</a>
		{/if}
		<div class="min-w-0 flex-1 {back ? '' : 'pl-1'}">
			<h1 class="truncate text-lg leading-tight font-semibold tracking-tight">{title}</h1>
			{#if subtitle}
				<p class="truncate text-xs text-base-content/60">{subtitle}</p>
			{/if}
		</div>
		{#if action || gear}
			<div class="flex items-center gap-1">
				{#if action}{@render action()}{/if}
				{#if gear}
					<a
						href="/settings"
						class="btn btn-circle min-h-11 min-w-11 btn-ghost btn-sm"
						aria-label="Settings"
						data-testid="settings-gear"
					>
						<Icon name="settings" size={22} />
					</a>
				{/if}
			</div>
		{/if}
	</div>
</header>
