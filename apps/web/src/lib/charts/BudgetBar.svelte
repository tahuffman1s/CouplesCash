<script lang="ts">
	import { formatMoney } from '#lib/utils/money.ts';

	let {
		spent,
		budget,
		color,
		label
	}: {
		spent: number;
		/** null → unbudgeted (bar shows relative to `max` instead) */
		budget: number | null;
		color: string;
		label: string;
	} = $props();

	const over = $derived(budget !== null && spent > budget);
	const pct = $derived(
		budget === null ? 0 : budget > 0 ? Math.min(100, (spent / budget) * 100) : spent > 0 ? 100 : 0
	);
	const fill = $derived(over ? 'var(--color-error)' : color);
</script>

<!-- Meter: ≤ 24px thick, square at the baseline, rounded data-end; track = lighter same-hue step -->
<div
	class="h-2.5 w-full overflow-hidden rounded-full"
	style:background="color-mix(in oklab, {fill} 18%, transparent)"
	role="meter"
	aria-valuemin="0"
	aria-valuemax={budget ?? spent}
	aria-valuenow={spent}
	aria-label="{label}: {formatMoney(spent)} of {budget === null
		? 'no budget'
		: formatMoney(budget)}{over ? ', over budget' : ''}"
>
	<div
		class="h-full rounded-r-full transition-[width] duration-500 motion-reduce:transition-none"
		style:width="{pct}%"
		style:background={fill}
	></div>
</div>
