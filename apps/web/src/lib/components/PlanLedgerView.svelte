<script lang="ts">
	import type { Plan, PlanLedger, PlanLedgerEntry } from '#lib/types.ts';
	import Icon from './Icon.svelte';
	import PlanLineRow from './PlanLineRow.svelte';
	import { formatMoney, signedMoney } from '#lib/utils/money.ts';
	import { bankDataAge, shortDay, upcomingCents, upcomingGroups } from '#lib/utils/plan.ts';

	/**
	 * v7 the current pay week as a ledger, top to bottom: the amount at the start of the week (the
	 * paycheck that landed on payday included), each day since payday folded up (tap to see what went
	 * through the bills account — a transaction that paid a bill shows as that bill, once), today
	 * highlighted (also folded) and ending in available now, then every day still to come, folded up
	 * with its count and total. Each transaction wears its category's icon and colour.
	 */
	let { plan, ledger }: { plan: Plan; ledger: PlanLedger } = $props();

	const b = $derived(plan.billsAccount!);
	const bankAge = $derived(bankDataAge(b.balanceAt));
	const groups = $derived(upcomingGroups(ledger.upcoming, plan.today, plan.end));
	const paychecks = $derived(ledger.startPaychecks ?? []);
	// what was in the account before this payday's paycheck landed
	const carriedCents = $derived(
		ledger.startCents - paychecks.reduce((s, p) => s + p.amountCents, 0)
	);
	const past = $derived(ledger.days.filter((d) => d.date !== plan.today));
	const todays = $derived(ledger.days.find((d) => d.date === plan.today)?.entries ?? []);

	const fallbackIcon = (e: PlanLedgerEntry) =>
		e.kind === 'transaction'
			? 'receipt'
			: e.kind === 'income'
				? 'banknote'
				: e.kind === 'transfer'
					? 'arrow-left-right'
					: 'circle-check';
	const tag = (e: PlanLedgerEntry) =>
		e.kind === 'transaction'
			? null
			: e.kind === 'income'
				? 'Paycheck'
				: e.kind === 'transfer'
					? 'Fun money'
					: e.kind === 'oneoff'
						? 'One-off'
						: 'Bill paid';
	const subline = (e: PlanLedgerEntry) =>
		[
			e.categoryName && e.kind === 'transaction' ? e.categoryName : null,
			e.pending ? 'pending' : null,
			e.added ? 'added by you' : null
		]
			.filter(Boolean)
			.join(' · ');
	const dayTotal = (entries: PlanLedgerEntry[]) => entries.reduce((s, e) => s + e.amountCents, 0);
	const countLabel = (n: number) => (n === 1 ? '1 transaction' : `${n} transactions`);
</script>

{#snippet entryRow(e: PlanLedgerEntry)}
	{@const t = tag(e)}
	{@const sub = subline(e)}
	<li class="flex min-h-12 items-center gap-3 py-2" data-testid="ledger-entry" data-kind={e.kind}>
		<span
			class="flex h-8 w-8 shrink-0 items-center justify-center rounded-xl {e.categoryColor
				? 'text-white'
				: e.kind === 'transaction'
					? 'bg-base-200 text-base-content/55'
					: 'bg-success/15 text-success'}"
			style:background={e.categoryColor ?? undefined}
			data-testid="ledger-entry-icon"
			data-icon={e.categoryIcon ?? fallbackIcon(e)}
		>
			<Icon name={e.categoryIcon ?? fallbackIcon(e)} size={16} />
		</span>
		<span class="min-w-0 flex-1">
			{#if e.itemId}
				<a href="/bills/{e.itemId}" class="block truncate text-sm font-medium">{e.name}</a>
			{:else}
				<span class="block truncate text-sm font-medium">{e.name}</span>
			{/if}
			{#if t || sub}
				<span class="block truncate text-xs text-base-content/60">
					{#if t}<span class="text-success">{t}</span>{/if}{t && sub ? ` · ${sub}` : sub}
				</span>
			{/if}
		</span>
		<span
			class="shrink-0 font-mono text-sm tabular {e.amountCents > 0 ? 'text-success' : ''}"
			data-testid="ledger-entry-amount">{signedMoney(e.amountCents)}</span
		>
	</li>
{/snippet}

<div class="flex flex-col px-4 pt-2 pb-2" data-testid="plan-ledger">
	<div class="-mx-2 mb-2 rounded-xl bg-base-200 px-3 py-3" data-testid="ledger-start">
		<div class="flex items-end gap-2">
			<span class="min-w-0 flex-1">
				<span class="block text-xs font-semibold tracking-wide text-base-content/60 uppercase"
					>Start of the week</span
				>
				<span class="block text-xs text-base-content/60">{shortDay(plan.start)} · payday</span>
			</span>
			<span
				class="shrink-0 font-mono text-2xl leading-tight font-bold tracking-tight tabular"
				data-testid="ledger-start-amount">{formatMoney(ledger.startCents)}</span
			>
		</div>
		{#if paychecks.length}
			<!-- how the start is made: what was left from last week + the paycheck that landed on payday -->
			<dl class="mt-2 border-t border-base-300 pt-2 text-sm" data-testid="ledger-start-breakdown">
				<div class="flex items-center gap-2 py-0.5">
					<dt class="min-w-0 flex-1 text-base-content/70">Left from last week</dt>
					<dd class="shrink-0 font-mono tabular" data-testid="ledger-start-carried">
						{formatMoney(carriedCents)}
					</dd>
				</div>
				{#each paychecks as p (p.transactionId)}
					<div class="flex items-center gap-2 py-0.5" data-testid="ledger-start-paycheck">
						<dt class="flex min-w-0 flex-1 items-center gap-1.5 text-base-content/70">
							<Icon name="banknote" size={14} class="shrink-0 text-success" />
							<span class="truncate">{p.name}</span>
						</dt>
						<dd class="shrink-0 font-mono text-success tabular">{signedMoney(p.amountCents)}</dd>
					</div>
				{/each}
			</dl>
		{/if}
	</div>

	{#each past as d (d.date)}
		<details
			class="group border-b border-dashed border-base-300"
			data-testid="ledger-day"
			data-date={d.date}
		>
			<summary
				class="flex min-h-12 cursor-pointer list-none items-center gap-2 py-2 [&::-webkit-details-marker]:hidden"
			>
				<span class="min-w-0 flex-1">
					<span class="block text-sm font-medium">{shortDay(d.date)}</span>
					<span class="block text-xs text-base-content/55" data-testid="ledger-day-count"
						>{d.entries.length ? countLabel(d.entries.length) : 'Nothing went through'}</span
					>
				</span>
				{#if d.entries.length}
					<span class="shrink-0 font-mono text-sm tabular" data-testid="ledger-day-total"
						>{signedMoney(dayTotal(d.entries))}</span
					>
				{/if}
				<Icon
					name="chevron-down"
					size={16}
					class="shrink-0 text-base-content/35 transition-transform group-open:rotate-180"
				/>
			</summary>
			{#if d.entries.length}
				<ul class="divide-y divide-base-300/60 pb-1">
					{#each d.entries as e (e.transactionId)}
						{@render entryRow(e)}
					{/each}
				</ul>
			{/if}
		</details>
	{/each}

	<section
		class="-mx-2 my-2 rounded-xl bg-primary/10 px-3 ring-1 ring-primary/40"
		data-testid="ledger-today"
	>
		<details class="group" data-testid="ledger-day" data-date={plan.today} data-today="true">
			<summary
				class="flex min-h-12 cursor-pointer list-none items-center gap-2 py-2 [&::-webkit-details-marker]:hidden"
			>
				<span class="min-w-0 flex-1">
					<span class="block text-xs font-semibold tracking-wide text-primary uppercase"
						>Today · {shortDay(plan.today)}</span
					>
					<span class="block text-xs text-base-content/55" data-testid="ledger-day-count"
						>{todays.length ? countLabel(todays.length) : 'Nothing went through yet'}</span
					>
				</span>
				{#if todays.length}
					<span class="shrink-0 font-mono text-sm tabular" data-testid="ledger-day-total"
						>{signedMoney(dayTotal(todays))}</span
					>
				{/if}
				<Icon
					name="chevron-down"
					size={16}
					class="shrink-0 text-base-content/35 transition-transform group-open:rotate-180"
				/>
			</summary>
			{#if todays.length}
				<ul class="divide-y divide-base-300/60">
					{#each todays as e (e.transactionId)}
						{@render entryRow(e)}
					{/each}
				</ul>
			{/if}
		</details>
		<div class="flex min-h-12 items-center gap-2 pb-2" data-testid="ledger-available">
			<Icon name="landmark" size={20} class="shrink-0 text-primary" />
			<span class="min-w-0 flex-1">
				<span class="block text-sm font-semibold">Available now</span>
				{#if bankAge?.stale}
					<span class="block text-xs font-medium text-warning" data-testid="bank-as-of"
						>{bankAge.label}</span
					>
				{/if}
			</span>
			<span
				class="shrink-0 font-mono text-base font-bold tabular"
				data-testid="plan-amount-available">{formatMoney(ledger.availableCents)}</span
			>
		</div>
	</section>

	{#each groups as g (g.key)}
		<details
			class="group border-b border-dashed border-base-300 last:border-b-0"
			data-testid="ledger-upcoming"
			data-date={g.date ?? 'before'}
		>
			<summary
				class="flex min-h-12 cursor-pointer list-none items-center gap-2 py-2 [&::-webkit-details-marker]:hidden"
			>
				<span class="min-w-0 flex-1">
					<span
						class="block text-sm font-medium {g.key === 'before'
							? 'text-warning'
							: g.today
								? 'text-primary'
								: ''}">{g.label}</span
					>
					<span class="block text-xs text-base-content/55" data-testid="ledger-upcoming-count"
						>{g.lines.length ? `${g.lines.length} due` : 'Nothing due'}</span
					>
				</span>
				{#if g.lines.length}
					<span class="shrink-0 font-mono text-sm tabular" data-testid="ledger-upcoming-total"
						>{signedMoney(upcomingCents(g.lines))}</span
					>
				{/if}
				<Icon
					name="chevron-down"
					size={16}
					class="shrink-0 text-base-content/35 transition-transform group-open:rotate-180"
				/>
			</summary>
			{#if g.lines.length}
				<ul class="divide-y divide-base-300/60 pb-1">
					{#each g.lines as l (`${l.occurrence.itemId}:${l.occurrence.dueDate}`)}
						<PlanLineRow line={l} today={plan.today} />
					{/each}
				</ul>
			{/if}
		</details>
	{/each}
</div>
