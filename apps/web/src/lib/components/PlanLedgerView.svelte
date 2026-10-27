<script lang="ts">
	import type { Plan, PlanLedger, PlanLedgerEntry } from '#lib/types.ts';
	import Icon from './Icon.svelte';
	import PlanLineRow from './PlanLineRow.svelte';
	import { formatMoney, signedMoney } from '#lib/utils/money.ts';
	import { bankDataAge, pendingLine, shortDay, upcomingGroups } from '#lib/utils/plan.ts';

	/**
	 * v7 the current pay week as a ledger, top to bottom: the amount at the start of the week, each day
	 * from payday through today with what went through the bills account (a transaction that paid a
	 * bill shows as that bill, once), today highlighted, available now set apart, then every bill
	 * still to come by day. The projected savings under it is start + what moved − what is to come.
	 */
	let { plan, ledger }: { plan: Plan; ledger: PlanLedger } = $props();

	const b = $derived(plan.billsAccount!);
	const bankAge = $derived(bankDataAge(b.balanceAt));
	const groups = $derived(upcomingGroups(ledger.upcoming, plan.today, plan.end));

	const entryIcon = (e: PlanLedgerEntry) =>
		e.kind === 'transaction' ? 'receipt' : e.kind === 'income' ? 'banknote' : 'circle-check';
	const entryTag = (e: PlanLedgerEntry) =>
		e.kind === 'transaction'
			? null
			: e.kind === 'income'
				? 'Paycheck'
				: e.kind === 'transfer'
					? 'Fun money'
					: e.kind === 'oneoff'
						? 'One-off'
						: 'Bill paid';
</script>

<div class="flex flex-col px-4 pt-2 pb-2" data-testid="plan-ledger">
	<div
		class="flex min-h-12 items-center gap-2 border-b border-dashed border-base-300 py-2"
		data-testid="ledger-start"
	>
		<span class="min-w-0 flex-1">
			<span class="block text-sm font-semibold">Start of the week</span>
			<span class="block text-xs text-base-content/60">{shortDay(plan.start)} · payday</span>
		</span>
		<span
			class="shrink-0 font-mono text-[0.95rem] font-semibold tabular"
			data-testid="ledger-start-amount">{formatMoney(ledger.startCents)}</span
		>
	</div>

	{#each ledger.days as d (d.date)}
		{@const isToday = d.date === plan.today}
		<section
			class="py-2 {isToday
				? '-mx-2 my-1 rounded-xl bg-primary/10 px-2 ring-1 ring-primary/40'
				: 'border-b border-dashed border-base-300'}"
			data-testid="ledger-day"
			data-date={d.date}
			data-today={isToday ? 'true' : undefined}
		>
			<h3
				class="text-xs font-semibold tracking-wide uppercase {isToday
					? 'text-primary'
					: 'text-base-content/60'}"
			>
				{isToday ? `Today · ${shortDay(d.date)}` : shortDay(d.date)}
			</h3>
			{#if d.entries.length === 0}
				<p class="py-1 text-xs text-base-content/50">Nothing went through.</p>
			{:else}
				<ul class="divide-y divide-base-300/60">
					{#each d.entries as e (e.transactionId)}
						{@const tag = entryTag(e)}
						<li
							class="flex min-h-11 items-center gap-2 py-1.5"
							data-testid="ledger-entry"
							data-kind={e.kind}
						>
							<Icon
								name={entryIcon(e)}
								size={18}
								class="shrink-0 {e.kind === 'transaction'
									? 'text-base-content/40'
									: 'text-success'}"
							/>
							<span class="min-w-0 flex-1">
								{#if e.itemId}
									<a href="/bills/{e.itemId}" class="block truncate text-sm font-medium">{e.name}</a
									>
								{:else}
									<span class="block truncate text-sm">{e.name}</span>
								{/if}
								{#if tag || e.pending || e.added}
									<span class="block truncate text-xs text-base-content/60">
										{#if tag}<span class="text-success">{tag}</span>{/if}{#if e.pending}{tag
												? ' · '
												: ''}pending{/if}{#if e.added}{tag || e.pending ? ' · ' : ''}added by you{/if}
									</span>
								{/if}
							</span>
							<span class="shrink-0 font-mono text-sm tabular" data-testid="ledger-entry-amount"
								>{signedMoney(e.amountCents)}</span
							>
						</li>
					{/each}
				</ul>
			{/if}
		</section>
	{/each}

	<div
		class="my-2 flex min-h-14 items-center gap-2 rounded-xl border-2 border-base-content/70 bg-base-200 px-3 py-2"
		data-testid="ledger-available"
	>
		<Icon name="landmark" size={20} class="shrink-0 text-base-content/70" />
		<span class="min-w-0 flex-1">
			<span class="block text-sm font-semibold">{b.name}, available now</span>
			<span class="block text-xs text-base-content/60" data-testid="pending-subline"
				>{pendingLine(b)}</span
			>
			{#if bankAge?.stale}
				<span class="block text-xs font-medium text-warning" data-testid="bank-as-of"
					>{bankAge.label}</span
				>
			{/if}
		</span>
		<span class="shrink-0 font-mono text-base font-bold tabular" data-testid="plan-amount-available"
			>{formatMoney(ledger.availableCents)}</span
		>
	</div>

	{#each groups as g (g.key)}
		<section
			class="border-b border-dashed border-base-300 py-2 last:border-b-0"
			data-testid="ledger-upcoming"
			data-date={g.date ?? 'before'}
		>
			<h3
				class="text-xs font-semibold tracking-wide uppercase {g.key === 'before'
					? 'text-warning'
					: g.today
						? 'text-primary'
						: 'text-base-content/60'}"
			>
				{g.label}
			</h3>
			{#if g.lines.length === 0}
				<p class="py-1 text-xs text-base-content/50">Nothing due.</p>
			{:else}
				<ul class="divide-y divide-base-300/60">
					{#each g.lines as l (`${l.occurrence.itemId}:${l.occurrence.dueDate}`)}
						<PlanLineRow line={l} today={plan.today} />
					{/each}
				</ul>
			{/if}
		</section>
	{/each}
</div>
