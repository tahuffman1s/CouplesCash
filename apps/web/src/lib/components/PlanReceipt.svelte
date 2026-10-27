<script lang="ts">
	import { enhance } from '$app/forms';
	import type { Plan } from '#lib/types.ts';
	import CopyButton from './CopyButton.svelte';
	import Icon from './Icon.svelte';
	import PlanLineRow from './PlanLineRow.svelte';
	import { formatDate } from '#lib/utils/dates.ts';
	import { formatMoney, signedMoney } from '#lib/utils/money.ts';
	import {
		leftLabel,
		pendingLine,
		receiptLines,
		savingsNote,
		sweepState
	} from '#lib/utils/plan.ts';

	/**
	 * v7 Friday plan: the calculation she does in her notes app, as a receipt. Monospace amounts,
	 * every line expands (pending, each bill with its tick, fun money per person, one-offs), and
	 * the hero "= Left on Thursday" with Copy. Under it, the bottom line: on Thursday what is left
	 * moves to savings (Copy / Done; ticks itself when the bank shows it). Savings bills sit under
	 * it, not in it.
	 */
	let { plan }: { plan: Plan } = $props();

	const sweep = $derived(sweepState(plan));
	let sweepBusy = $state(false);
	const sweepSubmit = () => {
		sweepBusy = true;
		return async ({ update }: { update: (o?: { reset?: boolean }) => Promise<void> }) => {
			await update({ reset: false });
			sweepBusy = false;
		};
	};

	const b = $derived(plan.billsAccount!);
	const lines = $derived(receiptLines(plan));
	const negative = $derived(plan.leftAtEndCents < 0);
	const amount = (key: string, cents: number) =>
		key === 'available' ? formatMoney(cents) : signedMoney(cents);
	const emptyText: Record<string, string> = {
		bills: 'Nothing else due before payday.',
		fun: 'No fun money this week.',
		oneoffs: 'No one-offs planned.',
		income: ''
	};
</script>

<section
	class="card overflow-hidden border border-base-300/70 bg-base-100 shadow-sm"
	aria-labelledby="plan-h"
	data-testid="plan-receipt"
>
	<h2 id="plan-h" class="sr-only">This week's calculation</h2>
	<div class="flex flex-col px-4 pt-2 pb-1">
		{#each lines as l (l.key)}
			<details class="group border-b border-dashed border-base-300" data-testid="plan-row-{l.key}">
				<summary
					class="flex min-h-14 cursor-pointer list-none items-center gap-2 py-2 [&::-webkit-details-marker]:hidden"
				>
					<span class="w-3 shrink-0 font-mono text-base-content/60" aria-hidden="true">{l.op}</span>
					<span class="min-w-0 flex-1">
						<span class="block text-sm leading-snug font-medium">{l.label}</span>
						{#if l.key === 'available'}
							<span class="block text-xs text-base-content/60" data-testid="pending-subline"
								>{pendingLine(b)}</span
							>
						{/if}
					</span>
					<span
						class="shrink-0 font-mono text-[0.95rem] font-semibold tabular"
						data-testid="plan-amount-{l.key}">{amount(l.key, l.cents)}</span
					>
					<Icon
						name="chevron-down"
						size={16}
						class="shrink-0 text-base-content/35 transition-transform group-open:rotate-180"
					/>
				</summary>
				<div class="pb-3 pl-5">
					{#if l.key === 'available'}
						<p class="text-xs text-base-content/65">
							{#if b.availableFromBank}
								What your bank says you can spend now. Pending charges are already taken out, so
								they're listed here, not subtracted again.
							{:else}
								Your bank doesn't report an available balance, so this is the balance
								{formatMoney(b.balanceCents)} minus what's pending.
							{/if}
						</p>
						{#if b.pending.length}
							<ul class="mt-1 divide-y divide-base-300/60" data-testid="plan-pending">
								{#each b.pending as t (t.id)}
									<li class="flex min-h-10 items-center gap-2 py-1 text-sm">
										<span class="min-w-0 flex-1 truncate"
											>{t.payee || t.description}{#if t.source === 'added'}<span
													class="ml-1.5 badge border-0 bg-base-200 px-1.5 badge-xs text-[0.65rem] text-base-content/70"
													data-testid="pending-added">added by you</span
												>{/if}</span
										>
										<span class="shrink-0 text-xs text-base-content/55"
											>{formatDate(t.postedOn)}</span
										>
										<span class="shrink-0 font-mono tabular">{signedMoney(t.amountCents)}</span>
									</li>
								{/each}
							</ul>
						{/if}
					{:else if l.lines.length === 0}
						<p class="text-xs text-base-content/60">{emptyText[l.key]}</p>
					{:else}
						<ul class="divide-y divide-base-300/60">
							{#each l.lines as pl (`${pl.occurrence.itemId}:${pl.occurrence.dueDate}`)}
								<PlanLineRow line={pl} today={plan.today} />
							{/each}
						</ul>
						{#if l.key === 'bills' || l.key === 'oneoffs' || l.key === 'fun'}
							<p class="mt-1 text-xs text-base-content/55">
								A bill counts until the bank shows it, so ticking one early never changes the
								number. Bills tick themselves when the bank shows them.
							</p>
						{/if}
					{/if}
				</div>
			</details>
		{/each}
	</div>
	<div
		class="flex flex-wrap items-center gap-x-3 gap-y-2 border-t-2 border-base-content/70 px-4 py-4 {negative
			? 'bg-error/10'
			: 'bg-primary/5'}"
	>
		<div class="min-w-0 flex-1">
			<p class="text-sm font-semibold">
				<span class="font-mono text-base-content/60" aria-hidden="true">=</span>
				{leftLabel(plan.end)}
			</p>
			<p
				class="font-mono text-4xl leading-tight font-bold tracking-tight tabular {negative
					? 'text-error'
					: ''}"
				data-testid="left-on"
			>
				{formatMoney(plan.leftAtEndCents)}
			</p>
		</div>
		<CopyButton cents={plan.leftAtEndCents} testid="copy-left" />
	</div>
	{#if sweep.kind !== 'none'}
		<div
			class="flex flex-wrap items-center gap-x-3 gap-y-2 border-t border-base-300/70 px-4 py-3"
			data-testid="sweep-line"
			data-state={sweep.kind}
		>
			<Icon
				name={sweep.kind === 'done' ? 'circle-check' : 'piggy-bank'}
				size={20}
				class="shrink-0 {sweep.kind === 'done' ? 'text-success' : 'text-primary'}"
			/>
			<p class="min-w-0 flex-1 text-sm" data-testid="sweep-text">
				{#if sweep.kind === 'later'}
					<span class="text-base-content/70"
						>On {sweep.day}, move what's left to savings{sweep.cents > 0
							? ` (${formatMoney(sweep.cents)} so far)`
							: ''}.</span
					>
				{:else if sweep.kind === 'due'}
					<span class="font-semibold"
						>Move {formatMoney(sweep.cents)} to savings{sweep.today ? ' today' : ''}</span
					>
				{:else if sweep.kind === 'nothing'}
					<span class="text-base-content/70"
						>Nothing to move to savings this week{sweep.leftCents < 0
							? ` (${signedMoney(sweep.leftCents)})`
							: ''}.</span
					>
				{:else}
					<span class="font-medium">Moved {formatMoney(sweep.cents)} to savings ✓</span>
					{#if sweep.auto}
						<span
							class="ml-1 badge border-0 bg-info/15 px-1.5 badge-xs text-[0.65rem] text-info"
							data-testid="sweep-auto">auto</span
						>
					{/if}
				{/if}
			</p>
			{#if sweep.kind === 'due'}
				<div class="flex shrink-0 gap-1.5">
					<CopyButton cents={sweep.cents} testid="copy-sweep" />
					<form method="POST" action="?/sweep" use:enhance={sweepSubmit}>
						<input type="hidden" name="weekStart" value={sweep.weekStart} />
						<input type="hidden" name="done" value="1" />
						<button
							class="btn min-h-11 rounded-full px-4 btn-primary btn-sm"
							disabled={sweepBusy}
							data-testid="sweep-done"
						>
							{#if sweepBusy}<span class="loading loading-xs loading-spinner"></span>{/if}
							Done
						</button>
					</form>
				</div>
			{:else if sweep.kind === 'done' && !sweep.auto}
				<form method="POST" action="?/sweep" use:enhance={sweepSubmit}>
					<input type="hidden" name="weekStart" value={sweep.weekStart} />
					<input type="hidden" name="done" value="0" />
					<button
						class="btn min-h-11 rounded-full btn-ghost px-3 text-xs btn-sm"
						disabled={sweepBusy}
						data-testid="sweep-undo">Undo</button
					>
				</form>
			{/if}
		</div>
	{/if}
</section>

{#if plan.savingsBills.length}
	<p class="px-1 text-xs text-base-content/65" data-testid="savings-note">
		<Icon name="piggy-bank" size={14} class="inline align-text-bottom" />
		From savings:
		{#each plan.savingsBills as s, i (`${s.occurrence.itemId}:${s.occurrence.dueDate}`)}
			<span class={s.occurrence.paid ? 'line-through' : ''}>{savingsNote(s)}</span
			>{#if i < plan.savingsBills.length - 1}<span aria-hidden="true" class="mx-1">·</span>{/if}
		{/each}
	</p>
{/if}
