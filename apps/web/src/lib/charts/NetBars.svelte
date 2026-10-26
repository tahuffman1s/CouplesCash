<script lang="ts">
	import type { TransferNet } from '#lib/types.ts';
	import { formatMoney } from '#lib/utils/money.ts';
	import { signedAmount } from '#lib/utils/labels.ts';
	import { netBars } from '#lib/utils/transactions.ts';

	let {
		rows,
		title = 'Transfers in and out by account'
	}: {
		rows: TransferNet[];
		title?: string;
	} = $props();

	// Diverging bars around one zero baseline: money out grows left (red pole), money in grows
	// right (blue pole). Polarity is the job (dataviz: diverging = two opposite hues, neutral
	// midpoint), validated with the palette script for both surfaces. Values are direct-labelled
	// at the bar ends in text ink, there is a legend, and a table view for every number.
	const bars = $derived(netBars(rows));
	let hover = $state<number | null>(null);
	let showTable = $state(false);
	const focus = $derived(hover !== null ? (bars[hover] ?? null) : null);
</script>

{#if bars.length > 0}
	<figure
		class="viz-net flex w-full flex-col items-stretch justify-start gap-2"
		data-testid="net-by-account"
	>
		<div class="flex items-center gap-4 text-xs text-base-content/70" aria-hidden="true">
			<span class="flex items-center gap-1.5"
				><span class="h-2.5 w-3 rounded-sm" style:background="var(--viz-out)"></span> Out</span
			>
			<span class="flex items-center gap-1.5"
				><span class="h-2.5 w-3 rounded-sm" style:background="var(--viz-in)"></span> In</span
			>
		</div>
		<ul class="flex flex-col gap-3" aria-label={title} onpointerleave={() => (hover = null)}>
			{#each bars as r, i (r.accountId)}
				<li
					class="flex flex-col gap-1"
					aria-label="{r.name}: in {formatMoney(r.inCents)}, out {formatMoney(
						r.outCents
					)}, net {signedAmount(r.netCents, formatMoney)}"
					data-testid="net-row"
					onpointerenter={() => (hover = i)}
				>
					<div class="flex items-baseline justify-between text-sm">
						<span class="truncate font-medium">{r.name}</span>
						<span class="text-xs text-base-content/70 tabular" data-testid="net-value"
							>net {signedAmount(r.netCents, formatMoney)}</span
						>
					</div>
					<div
						class="grid grid-cols-2 items-center"
						style:opacity={hover === null || hover === i ? 1 : 0.5}
					>
						<!-- out: label at the outer edge, bar grows leftwards from the baseline (square there,
						     4px round at the data end); width is a true share of the track, no label offset -->
						<div class="flex h-5 items-center gap-1.5">
							<span class="w-16 shrink-0 text-left text-[0.7rem] text-base-content/70 tabular"
								>{r.outCents > 0 ? formatMoney(r.outCents) : ''}</span
							>
							<span class="flex h-full min-w-0 flex-1 items-center justify-end">
								<span
									class="block h-3.5 rounded-l transition-[width] duration-500 motion-reduce:transition-none"
									style:width="{r.outPct}%"
									style:min-width={r.outCents > 0 ? '2px' : '0'}
									style:background="var(--viz-out)"
								></span>
							</span>
						</div>
						<!-- in: grows rightwards from the baseline, label at the outer edge -->
						<div class="flex h-5 items-center gap-1.5 border-l border-base-content/25">
							<span class="flex h-full min-w-0 flex-1 items-center">
								<span
									class="block h-3.5 rounded-r transition-[width] duration-500 motion-reduce:transition-none"
									style:width="{r.inPct}%"
									style:min-width={r.inCents > 0 ? '2px' : '0'}
									style:background="var(--viz-in)"
								></span>
							</span>
							<span class="w-16 shrink-0 text-right text-[0.7rem] text-base-content/70 tabular"
								>{r.inCents > 0 ? formatMoney(r.inCents) : ''}</span
							>
						</div>
					</div>
				</li>
			{/each}
		</ul>
		<table class="table mt-1 table-xs {showTable ? '' : 'sr-only'}" aria-label={title}>
			<thead>
				<tr
					><th>Account</th><th class="text-right">In</th><th class="text-right">Out</th><th
						class="text-right">Net</th
					></tr
				>
			</thead>
			<tbody>
				{#each rows as r (r.accountId)}
					<tr>
						<td>{r.name}</td>
						<td class="text-right tabular">{formatMoney(r.inCents)}</td>
						<td class="text-right tabular">{formatMoney(r.outCents)}</td>
						<td class="text-right tabular">{signedAmount(r.netCents, formatMoney)}</td>
					</tr>
				{/each}
			</tbody>
		</table>
		<figcaption class="flex min-h-5 justify-between gap-3 text-xs text-base-content/60">
			{#if focus}
				<span class="truncate" aria-live="polite">{focus.name}</span>
				<span class="font-medium text-base-content tabular">
					in {formatMoney(focus.inCents)} · out {formatMoney(focus.outCents)}
				</span>
			{:else}
				<span>Left of the line moved out, right moved in</span>
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

<style>
	/* diverging pair from the dataviz reference palette (blue ↔ red), stepped per surface */
	.viz-net {
		--viz-in: #2a78d6;
		--viz-out: #e34948;
	}
	@media (prefers-color-scheme: dark) {
		:global(:root:not([data-theme='couplescash'])) .viz-net {
			--viz-in: #3987e5;
			--viz-out: #e66767;
		}
	}
	:global(:root[data-theme='couplescash-dark']) .viz-net {
		--viz-in: #3987e5;
		--viz-out: #e66767;
	}
</style>
