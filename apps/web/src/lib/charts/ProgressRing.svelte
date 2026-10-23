<script lang="ts">
	import { onMount } from 'svelte';
	import { prefersReducedMotion } from '#lib/state/motion.svelte.ts';

	let {
		percent,
		color = 'var(--color-primary)',
		size = 64,
		thickness = 7,
		label,
		children
	}: {
		/** 0..100 (values above 100 fill the ring) */
		percent: number;
		color?: string;
		size?: number;
		thickness?: number;
		/** accessible description of the ring */
		label: string;
		children?: import('svelte').Snippet;
	} = $props();

	const r = $derived((size - thickness) / 2);
	const circ = $derived(2 * Math.PI * r);
	const frac = $derived(Math.min(1, Math.max(0, percent / 100)));

	let progress = $state(0);
	onMount(() => {
		if (prefersReducedMotion()) {
			progress = 1;
			return;
		}
		const start = performance.now();
		let raf = 0;
		const tick = (t: number) => {
			const p = Math.min(1, (t - start) / 600);
			progress = 1 - Math.pow(1 - p, 3);
			if (p < 1) raf = requestAnimationFrame(tick);
		};
		raf = requestAnimationFrame(tick);
		return () => cancelAnimationFrame(raf);
	});
</script>

<!-- Meter: the track is a lighter step of the same hue so state reads across the whole ring -->
<div class="relative inline-flex shrink-0 items-center justify-center" style:width="{size}px">
	<svg
		width={size}
		height={size}
		viewBox="0 0 {size} {size}"
		role="meter"
		aria-valuemin="0"
		aria-valuemax="100"
		aria-valuenow={Math.round(percent)}
		aria-label={label}
		class="block"
	>
		<circle
			cx={size / 2}
			cy={size / 2}
			{r}
			fill="none"
			stroke={color}
			stroke-opacity="0.18"
			stroke-width={thickness}
		/>
		<circle
			cx={size / 2}
			cy={size / 2}
			{r}
			fill="none"
			stroke={color}
			stroke-width={thickness}
			stroke-linecap="round"
			stroke-dasharray="{circ} {circ}"
			stroke-dashoffset={circ * (1 - frac * progress)}
			transform="rotate(-90 {size / 2} {size / 2})"
		/>
	</svg>
	{#if children}
		<div class="absolute inset-0 flex items-center justify-center">{@render children()}</div>
	{/if}
</div>
