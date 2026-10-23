<script lang="ts">
	import type { Snippet } from 'svelte';
	import Icon from './Icon.svelte';

	let {
		icon = 'sparkles',
		title,
		body,
		cta,
		href,
		children
	}: {
		icon?: string;
		title: string;
		body?: string;
		cta?: string;
		href?: string;
		children?: Snippet;
	} = $props();
</script>

<div class="card border border-base-300/60 bg-base-100 shadow-sm">
	<div class="card-body items-center px-6 py-10 text-center">
		<div
			class="mb-2 flex h-16 w-16 items-center justify-center rounded-full bg-primary/10 text-primary"
		>
			<Icon name={icon} size={30} />
		</div>
		<h2 class="text-lg font-semibold">{title}</h2>
		{#if body}<p class="max-w-xs text-sm text-base-content/65">{body}</p>{/if}
		{#if cta && href}
			<a {href} class="btn mt-4 min-h-11 rounded-full px-6 btn-primary">{cta}</a>
		{/if}
		{#if children}{@render children()}{/if}
	</div>
</div>
