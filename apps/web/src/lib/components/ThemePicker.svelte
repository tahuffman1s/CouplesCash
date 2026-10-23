<script lang="ts">
	import { untrack } from 'svelte';
	import { enhance } from '$app/forms';
	import type { Theme } from '#lib/server/cookies.ts';
	import Icon from './Icon.svelte';

	let { theme }: { theme: Theme } = $props();
	let current = $state<Theme>(untrack(() => theme));

	const options: { value: Theme; label: string; icon: string }[] = [
		{ value: 'system', label: 'System', icon: 'monitor' },
		{ value: 'light', label: 'Light', icon: 'sun' },
		{ value: 'dark', label: 'Dark', icon: 'moon' }
	];

	/** Apply immediately on the client; the form action persists the cookie server-side too. */
	function apply(t: Theme) {
		current = t;
		const html = document.documentElement;
		if (t === 'system') delete html.dataset.theme;
		else html.dataset.theme = t === 'dark' ? 'couplescash-dark' : 'couplescash';
		const secure = location.protocol === 'https:' ? '; Secure' : '';
		document.cookie = `theme=${t}; Path=/; Max-Age=31536000; SameSite=Lax${secure}`;
	}
</script>

<form
	method="POST"
	action="?/theme"
	use:enhance={({ formData }) => {
		apply((formData.get('theme') as Theme) ?? 'system');
		return async ({ update }) => update({ reset: false, invalidateAll: false });
	}}
>
	<div class="join w-full" role="radiogroup" aria-label="Theme">
		{#each options as o (o.value)}
			<button
				name="theme"
				value={o.value}
				role="radio"
				aria-checked={current === o.value}
				class="btn join-item min-h-12 flex-1 gap-1.5 {current === o.value
					? 'btn-primary'
					: 'border border-base-300 btn-ghost'}"
				data-testid="theme-{o.value}"
			>
				<Icon name={o.icon} size={18} />
				{o.label}
			</button>
		{/each}
	</div>
</form>
