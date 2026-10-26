<script lang="ts">
	import { flowTone, signedMoney } from '#lib/utils/money.ts';

	/** A signed, coloured money flow: "−$12.49" (out) / "+$3,200.00" (in). */
	let {
		cents,
		whole = false,
		muted = false,
		class: klass = '',
		testid
	}: {
		/** signed cents: negative = money out */
		cents: number;
		whole?: boolean;
		/** paid/ignored rows: keep the sign, drop the colour */
		muted?: boolean;
		class?: string;
		testid?: string;
	} = $props();
</script>

<span
	class="tabular {muted ? 'text-base-content/50' : flowTone(cents)} {klass}"
	data-testid={testid}>{signedMoney(cents, { whole })}</span
>
