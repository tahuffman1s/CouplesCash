<script lang="ts">
	import { onMount } from 'svelte';
	import { formatMoney } from '#lib/utils/money.ts';
	import { prefersReducedMotion } from '#lib/state/motion.svelte.ts';

	export type DonutSlice = { id: string; label: string; value: number; color: string };

	let {
		slices,
		size = 200,
		thickness = 22,
		title = 'Breakdown',
		centerLabel = 'per month',
		selected = $bindable<string | null>(null),
		maxSlices = 6,
		legend = true
	}: {
		slices: DonutSlice[];
		size?: number;
		thickness?: number;
		title?: string;
		centerLabel?: string;
		/** id of the highlighted slice (two-way bindable; tapping toggles it) */
		selected?: string | null;
		/** slices beyond this fold into "Other" so the ring stays readable */
		maxSlices?: number;
		/** hide the legend when a list beside the chart already names every slice */
		legend?: boolean;
	} = $props();

	const OTHER = '__other__';

	const folded = $derived.by(() => {
		const sorted = [...slices].filter((s) => s.value > 0).sort((a, b) => b.value - a.value);
		if (sorted.length <= maxSlices) return sorted;
		const head = sorted.slice(0, maxSlices - 1);
		const rest = sorted.slice(maxSlices - 1);
		return [
			...head,
			{
				id: OTHER,
				label: `Other (${rest.length})`,
				value: rest.reduce((a, s) => a + s.value, 0),
				color: 'var(--color-base-300)'
			}
		];
	});

	const total = $derived(folded.reduce((a, s) => a + s.value, 0));
	const r = $derived((size - thickness) / 2);
	const circumference = $derived(2 * Math.PI * r);
	const gap = 2; // px of surface between arcs

	const arcs = $derived.by(() => {
		let offset = 0;
		return folded.map((s) => {
			const frac = total > 0 ? s.value / total : 0;
			const len = Math.max(0, frac * circumference - (folded.length > 1 ? gap : 0));
			const arc = { ...s, frac, dash: `${len} ${circumference - len}`, offset: -offset };
			offset += frac * circumference;
			return arc;
		});
	});

	let progress = $state(0);
	onMount(() => {
		if (prefersReducedMotion()) {
			progress = 1;
			return;
		}
		const start = performance.now();
		const dur = 650;
		let raf = 0;
		const tick = (t: number) => {
			const p = Math.min(1, (t - start) / dur);
			progress = 1 - Math.pow(1 - p, 3);
			if (p < 1) raf = requestAnimationFrame(tick);
		};
		raf = requestAnimationFrame(tick);
		return () => cancelAnimationFrame(raf);
	});

	const active = $derived(arcs.find((a) => a.id === selected) ?? null);
	const summary = $derived(
		arcs.map((a) => `${a.label} ${formatMoney(a.value)} (${Math.round(a.frac * 100)}%)`).join(', ')
	);

	function toggle(id: string) {
		selected = selected === id ? null : id;
	}
</script>

<figure class="flex flex-col items-center gap-3">
	<svg
		width={size}
		height={size}
		viewBox="0 0 {size} {size}"
		role="img"
		aria-label="{title}: {summary || 'no data'}"
		class="block"
	>
		<title>{title}</title>
		<circle
			cx={size / 2}
			cy={size / 2}
			{r}
			fill="none"
			stroke="var(--color-base-300)"
			stroke-opacity="0.5"
			stroke-width={thickness}
		/>
		<g transform="rotate(-90 {size / 2} {size / 2})">
			{#each arcs as a (a.id)}
				<circle
					cx={size / 2}
					cy={size / 2}
					{r}
					fill="none"
					stroke={a.color}
					stroke-width={selected && selected !== a.id ? thickness - 6 : thickness}
					stroke-dasharray={a.dash}
					stroke-dashoffset={a.offset * progress + circumference * (1 - progress)}
					opacity={selected && selected !== a.id ? 0.35 : 1}
					class="cursor-pointer transition-[stroke-width,opacity] duration-200 motion-reduce:transition-none"
					role="button"
					tabindex="0"
					aria-label="{a.label}: {formatMoney(a.value)}"
					aria-pressed={selected === a.id}
					onclick={() => toggle(a.id)}
					onkeydown={(e) =>
						(e.key === 'Enter' || e.key === ' ') && (e.preventDefault(), toggle(a.id))}
				>
					<title>{a.label} · {formatMoney(a.value)} · {Math.round(a.frac * 100)}%</title>
				</circle>
			{/each}
		</g>
		<text
			x="50%"
			y="47%"
			text-anchor="middle"
			dominant-baseline="middle"
			class="fill-base-content text-[1.35rem] font-semibold"
		>
			{formatMoney(active ? active.value : total, { whole: true })}
		</text>
		<text
			x="50%"
			y="60%"
			text-anchor="middle"
			dominant-baseline="middle"
			class="fill-base-content/60 text-[0.7rem]"
		>
			{active ? active.label : centerLabel}
		</text>
	</svg>

	{#if legend && arcs.length > 1}
		<figcaption class="w-full">
			<ul class="flex flex-wrap justify-center gap-x-3 gap-y-1.5 text-xs" aria-label="Legend">
				{#each arcs as a (a.id)}
					<li>
						<button
							type="button"
							class="flex min-h-7 items-center gap-1.5 rounded-full px-2 {selected === a.id
								? 'bg-base-300/60'
								: ''}"
							aria-pressed={selected === a.id}
							onclick={() => toggle(a.id)}
						>
							<span class="inline-block h-2.5 w-2.5 rounded-full" style:background={a.color}></span>
							<span class="text-base-content/80">{a.label}</span>
						</button>
					</li>
				{/each}
			</ul>
		</figcaption>
	{/if}
</figure>
