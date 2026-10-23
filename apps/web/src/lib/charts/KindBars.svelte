<script lang="ts">
	import type { Kind } from '#lib/types.ts';
	import { KIND_LABEL_PLURAL } from '#lib/utils/labels.ts';
	import { formatMoney } from '#lib/utils/money.ts';

	let {
		rows
	}: {
		rows: { kind: Kind; amountCents: number; count: number }[];
	} = $props();

	const order: Kind[] = ['bill', 'subscription', 'income'];
	const sorted = $derived(
		order
			.map((k) => rows.find((r) => r.kind === k) ?? { kind: k, amountCents: 0, count: 0 })
			.filter((r) => r.count > 0 || r.amountCents > 0)
	);
	const max = $derived(Math.max(1, ...sorted.map((r) => r.amountCents)));
</script>

{#if sorted.length === 0}
	<p class="text-sm text-base-content/60">Nothing recurring yet.</p>
{:else}
	<ul class="space-y-3" aria-label="Monthly amount by kind">
		{#each sorted as r (r.kind)}
			<li>
				<div class="mb-1 flex items-baseline justify-between text-sm">
					<span class="font-medium">{KIND_LABEL_PLURAL[r.kind]}</span>
					<span class="text-xs text-base-content/60">{r.count} item{r.count === 1 ? '' : 's'}</span>
				</div>
				<div class="flex items-center gap-2">
					<div
						class="h-3 flex-1 overflow-hidden rounded-r bg-base-300/50"
						role="meter"
						aria-valuemin="0"
						aria-valuemax={max}
						aria-valuenow={r.amountCents}
						aria-label="{KIND_LABEL_PLURAL[r.kind]} {formatMoney(r.amountCents)} per month"
					>
						<div
							class="h-full rounded-r {r.kind === 'income'
								? 'bg-secondary'
								: 'bg-primary'} transition-[width] duration-500 motion-reduce:transition-none"
							style:width="{(r.amountCents / max) * 100}%"
						></div>
					</div>
					<span class="w-24 text-right text-sm font-medium tabular"
						>{formatMoney(r.amountCents)}</span
					>
				</div>
			</li>
		{/each}
	</ul>
{/if}
