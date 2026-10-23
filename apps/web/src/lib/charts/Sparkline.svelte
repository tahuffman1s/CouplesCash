<script lang="ts">
	import { formatMoney } from '#lib/utils/money.ts';
	import { formatTimestamp } from '#lib/utils/dates.ts';

	export type SparkPoint = { at: string; value: number };

	let {
		points,
		width = 320,
		height = 72,
		color = 'var(--color-primary)',
		title = 'Balance history'
	}: {
		/** oldest → newest */
		points: SparkPoint[];
		width?: number;
		height?: number;
		color?: string;
		title?: string;
	} = $props();

	const pad = 6;
	const geo = $derived.by(() => {
		if (points.length === 0) return null;
		const vals = points.map((p) => p.value);
		const min = Math.min(...vals);
		const max = Math.max(...vals);
		const span = max - min || 1;
		const stepX = points.length > 1 ? (width - pad * 2) / (points.length - 1) : 0;
		const xy = points.map((p, i) => ({
			x: pad + i * stepX,
			y: pad + (1 - (p.value - min) / span) * (height - pad * 2),
			p
		}));
		const path = xy
			.map((d, i) => `${i === 0 ? 'M' : 'L'}${d.x.toFixed(1)} ${d.y.toFixed(1)}`)
			.join(' ');
		const area = `${path} L${xy[xy.length - 1].x.toFixed(1)} ${height - pad} L${xy[0].x.toFixed(1)} ${height - pad} Z`;
		return { xy, path, area, last: xy[xy.length - 1] };
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
			aria-label="{title}: {points.length} points, latest {formatMoney(
				points[points.length - 1].value
			)}"
			onpointermove={onPointer}
			onpointerleave={() => (hover = null)}
		>
			<title>{title}</title>
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
			<span class="font-medium text-base-content tabular">
				{focus ? formatMoney(focus.p.value) : ''}
			</span>
		</p>
	</div>
{:else}
	<p class="text-sm text-base-content/60">No history yet. Update the balance to start a trend.</p>
{/if}
