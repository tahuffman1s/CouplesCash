<script lang="ts">
	import { formatMoney } from '#lib/utils/money.ts';
	import { formatTimestamp } from '#lib/utils/dates.ts';

	let {
		points,
		target,
		color = 'var(--color-primary)',
		width = 320,
		height = 96
	}: {
		/** oldest → newest */
		points: { at: string; value: number }[];
		target: number;
		color?: string;
		width?: number;
		height?: number;
	} = $props();

	const pad = 8;
	const geo = $derived.by(() => {
		// a single point (e.g. the creation marker) is not a trend yet
		if (points.length < 2) return null;
		const vals = [...points.map((p) => p.value), target, 0];
		const min = Math.min(...vals);
		const max = Math.max(...vals);
		const span = max - min || 1;
		const y = (v: number) => pad + (1 - (v - min) / span) * (height - pad * 2);
		const stepX = points.length > 1 ? (width - pad * 2) / (points.length - 1) : 0;
		const xy = points.map((p, i) => ({ x: pad + i * stepX, y: y(p.value), p }));
		const path = xy
			.map((d, i) => `${i === 0 ? 'M' : 'L'}${d.x.toFixed(1)} ${d.y.toFixed(1)}`)
			.join(' ');
		const area = `${path} L${xy[xy.length - 1].x.toFixed(1)} ${y(min).toFixed(1)} L${xy[0].x.toFixed(1)} ${y(min).toFixed(1)} Z`;
		return { xy, path, area, last: xy[xy.length - 1], targetY: y(target) };
	});

	let hover = $state<number | null>(null);
	const focus = $derived(geo && hover !== null ? geo.xy[hover] : (geo?.last ?? null));

	function onPointer(e: PointerEvent) {
		if (!geo) return;
		const rect = (e.currentTarget as SVGElement).getBoundingClientRect();
		const x = ((e.clientX - rect.left) / rect.width) * width;
		let best = 0;
		let dist = Infinity;
		geo.xy.forEach((d, i) => {
			const dd = Math.abs(d.x - x);
			if (dd < dist) {
				dist = dd;
				best = i;
			}
		});
		hover = best;
	}
</script>

{#if geo}
	<div class="w-full">
		<svg
			viewBox="0 0 {width} {height}"
			class="h-auto w-full touch-pan-y"
			role="img"
			aria-label="Goal history: {points.length} points, latest {formatMoney(
				points[points.length - 1].value
			)}, target {formatMoney(target)}"
			onpointermove={onPointer}
			onpointerleave={() => (hover = null)}
		>
			<title>Goal history</title>
			<!-- target line: the one dashed rule in the app, because it IS a threshold -->
			<line
				x1={pad}
				x2={width - pad}
				y1={geo.targetY}
				y2={geo.targetY}
				stroke="var(--color-base-content)"
				stroke-opacity="0.35"
				stroke-width="1"
				stroke-dasharray="4 4"
			/>
			<text
				x={width - pad}
				y={geo.targetY < 18 ? geo.targetY + 12 : geo.targetY - 4}
				text-anchor="end"
				class="fill-base-content/60 text-[0.6rem]"
			>
				Target {formatMoney(target, { whole: true })}
			</text>
			<path d={geo.area} fill={color} opacity="0.1" />
			<path
				d={geo.path}
				fill="none"
				stroke={color}
				stroke-width="2"
				stroke-linejoin="round"
				stroke-linecap="round"
			/>
			{#if focus}
				{#if hover !== null}
					<line
						x1={focus.x}
						x2={focus.x}
						y1={pad}
						y2={height - pad}
						stroke="var(--color-base-content)"
						stroke-opacity="0.2"
						stroke-width="1"
					/>
				{/if}
				<circle cx={focus.x} cy={focus.y} r="6" fill="var(--color-base-100)" />
				<circle cx={focus.x} cy={focus.y} r="4" fill={color} />
			{/if}
		</svg>
		<p class="mt-1 flex justify-between text-xs text-base-content/60" aria-live="polite">
			<span>{focus ? formatTimestamp(focus.p.at) : ''}</span>
			<span class="font-medium text-base-content tabular"
				>{focus ? formatMoney(focus.p.value) : ''}</span
			>
		</p>
	</div>
{:else}
	<p class="text-sm text-base-content/60">
		No history yet. Contribute (or update the linked account) to start the trend.
	</p>
{/if}
