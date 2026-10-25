<script lang="ts">
	import type { WeekBudgetHistory } from '#lib/types.ts';
	import { formatMoney } from '#lib/utils/money.ts';
	import { formatDate } from '#lib/utils/dates.ts';
	import { shortWeekLabel, weekEnd } from '#lib/utils/week.ts';

	let {
		history,
		currentStart,
		title = 'Over or under the weekly budget, last 8 weeks'
	}: {
		/** oldest → newest, the viewed week last */
		history: WeekBudgetHistory;
		/** the week being viewed (bold label) */
		currentStart?: string;
		title?: string;
	} = $props();

	// Diverging column chart around a zero baseline: over (positive variance) rises in the error
	// colour, under (negative) drops in the success colour. Polarity is the job, so status colours
	// are the right encoding here (dataviz: diverging = two opposite hues + a neutral midpoint).
	const width = 320;
	const height = 112;
	const padX = 8;
	const labelH = 18;
	const plotH = height - labelH;
	const baseline = plotH / 2;
	const barW = 18; // ≤ 24px, square at the baseline, rounded data-end
	const radius = 4;

	const geo = $derived.by(() => {
		if (history.length === 0) return null;
		const max = Math.max(1, ...history.map((h) => Math.abs(h.varianceCents)));
		const slot = (width - padX * 2) / history.length;
		return history.map((h, i) => {
			const x = padX + slot * i + (slot - barW) / 2;
			const len = (Math.abs(h.varianceCents) / max) * (baseline - 6);
			const over = h.varianceCents > 0;
			const under = h.varianceCents < 0;
			const y = over ? baseline - len : baseline;
			// rounded only at the data end; square at the baseline
			const r = Math.min(radius, len / 2, barW / 2);
			const path = over
				? `M${x} ${baseline} V${y + r} a${r} ${r} 0 0 1 ${r} ${-r} H${x + barW - r} a${r} ${r} 0 0 1 ${r} ${r} V${baseline} Z`
				: under
					? `M${x} ${baseline} V${baseline + len - r} a${r} ${r} 0 0 0 ${r} ${r} H${x + barW - r} a${r} ${r} 0 0 0 ${r} ${-r} V${baseline} Z`
					: '';
			return { ...h, x, y, len, over, under, path, cx: x + barW / 2 };
		});
	});

	let hover = $state<number | null>(null);
	let showTable = $state(false);
	const focus = $derived(geo && hover !== null ? geo[hover] : null);
	const describe = (h: WeekBudgetHistory[number]) =>
		h.varianceCents > 0
			? `over by ${formatMoney(h.varianceCents)}`
			: h.varianceCents < 0
				? `under by ${formatMoney(-h.varianceCents)}`
				: 'on target';
</script>

{#if geo}
	<figure class="flex w-full flex-col" data-testid="week-history">
		<svg
			viewBox="0 0 {width} {height}"
			class="block h-auto w-full touch-pan-y"
			role="img"
			aria-label="{title}. {geo.filter((g) => g.over).length} of {geo.length} weeks over budget."
			onpointerleave={() => (hover = null)}
		>
			<title>{title}</title>
			<!-- zero baseline: solid hairline, one step off the surface -->
			<line
				x1={padX}
				x2={width - padX}
				y1={baseline}
				y2={baseline}
				stroke="var(--color-base-content)"
				stroke-opacity="0.18"
				stroke-width="1"
			/>
			{#each geo as g, i (g.weekStart)}
				<g
					data-testid="week-history-bar"
					data-week={g.weekStart}
					data-variance={g.varianceCents}
					onpointerenter={() => (hover = i)}
					onfocus={() => (hover = i)}
					role="presentation"
				>
					<!-- hit target bigger than the mark -->
					<rect
						x={g.x - 6}
						y={0}
						width={barW + 12}
						height={plotH}
						fill="transparent"
						aria-hidden="true"
					/>
					{#if g.path}
						<path
							d={g.path}
							fill={g.over ? 'var(--color-error)' : 'var(--color-success)'}
							opacity={hover === null || hover === i ? 1 : 0.45}
						/>
					{:else}
						<!-- on target / no data: a dot on the baseline -->
						<circle
							cx={g.cx}
							cy={baseline}
							r="2.5"
							fill="var(--color-base-content)"
							fill-opacity="0.35"
						/>
					{/if}
					<text
						x={g.cx}
						y={height - 4}
						text-anchor="middle"
						font-size="10"
						fill="var(--color-base-content)"
						fill-opacity={g.weekStart === currentStart ? 0.9 : 0.55}
						font-weight={g.weekStart === currentStart ? 600 : 400}
					>
						{shortWeekLabel(g.weekStart)}
					</text>
				</g>
			{/each}
		</svg>
		<table class="table mt-2 table-xs {showTable ? '' : 'sr-only'}" aria-label={title}>
			<thead>
				<tr
					><th>Week of</th><th class="text-right">Spent</th><th class="text-right">Budget</th><th
						>Result</th
					></tr
				>
			</thead>
			<tbody>
				{#each history as h (h.weekStart)}
					<tr>
						<td>{formatDate(h.weekStart)}</td>
						<td class="text-right tabular">{formatMoney(h.spentCents)}</td>
						<td class="text-right tabular">{formatMoney(h.budgetCents)}</td>
						<td class={h.varianceCents > 0 ? 'text-error' : ''}>{describe(h)}</td>
					</tr>
				{/each}
			</tbody>
		</table>
		<figcaption class="mt-1 flex min-h-5 justify-between gap-3 text-xs text-base-content/60">
			{#if focus}
				<span aria-live="polite">
					{formatDate(focus.weekStart)} – {formatDate(weekEnd(focus.weekStart))}
				</span>
				<span class="font-medium tabular {focus.over ? 'text-error' : 'text-base-content'}">
					{formatMoney(focus.spentCents)} of {formatMoney(focus.budgetCents)} · {describe(focus)}
				</span>
			{:else}
				<span>Over the line is over budget · under is left over</span>
				<button
					type="button"
					class="link link-primary no-underline"
					aria-expanded={showTable}
					onclick={() => (showTable = !showTable)}
				>
					{showTable ? 'Hide table' : 'Table'}
				</button>
			{/if}
		</figcaption>
	</figure>
{/if}
