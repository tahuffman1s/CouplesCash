<script lang="ts">
	import { formatMoney, type MoneyOptions } from '#lib/utils/money.ts';

	let {
		cents,
		signed = false,
		whole = false,
		compact = false,
		tone = 'auto',
		class: klass = ''
	}: MoneyOptions & {
		cents: number;
		/** auto: colour negative values; none: inherit; positive/negative: force */
		tone?: 'auto' | 'none' | 'positive' | 'negative';
		class?: string;
	} = $props();

	const colour = $derived(
		tone === 'none'
			? ''
			: tone === 'positive' || (tone === 'auto' && signed && cents > 0)
				? 'text-success'
				: tone === 'negative' || (tone === 'auto' && cents < 0)
					? 'text-error'
					: ''
	);
</script>

<span class="tabular {colour} {klass}">{formatMoney(cents, { signed, whole, compact })}</span>
