<script lang="ts">
	import { invalidateAll } from '$app/navigation';
	import type { Occurrence } from '#lib/types.ts';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import NoHousehold from '#lib/components/NoHousehold.svelte';
	import OccurrenceRow from '#lib/components/OccurrenceRow.svelte';
	import MoneyText from '#lib/components/MoneyText.svelte';
	import Donut from '#lib/charts/Donut.svelte';
	import { formatMoney } from '#lib/utils/money.ts';
	import { formatMonth, today } from '#lib/utils/dates.ts';
	import { occurrenceKey, optimisticToggle, togglePaid } from '#lib/utils/occurrences.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data }: PageProps = $props();

	// Local, writable copy for optimistic "mark paid"; resets whenever the server data changes
	let upcoming = $derived<Occurrence[]>(data.summary?.upcoming ?? []);
	let busy = $state<string[]>([]);

	async function onToggle(occ: Occurrence) {
		const key = occurrenceKey(occ);
		const i = upcoming.findIndex((o) => occurrenceKey(o) === key);
		if (i < 0 || busy.includes(key)) return;
		const previous = upcoming[i];
		upcoming = upcoming.with(i, optimisticToggle(occ, data.user?.id ?? ''));
		busy = [...busy, key];
		try {
			upcoming = upcoming.with(i, await togglePaid(occ));
			toasts.show(
				upcoming[i].paid ? `${occ.name} marked paid` : `${occ.name} marked unpaid`,
				'success'
			);
			void invalidateAll();
		} catch {
			upcoming = upcoming.with(i, previous);
			toasts.show('Could not update. Check your connection.', 'error');
		} finally {
			busy = busy.filter((k) => k !== key);
		}
	}

	const firstName = $derived((data.user?.name ?? '').split(' ')[0]);
	const hour = new Date().getHours();
	const greeting = hour < 12 ? 'Good morning' : hour < 18 ? 'Good afternoon' : 'Good evening';
	const s = $derived(data.summary);
	const hasAnything = $derived(!!s && (s.accounts.length > 0 || s.byKind.some((k) => k.count > 0)));
	const paidPct = $derived(
		s && s.dueThisMonthCents > 0
			? Math.round((s.paidThisMonthCents / s.dueThisMonthCents) * 100)
			: 0
	);
	const donutSlices = $derived(
		(s?.byCategory ?? []).map((c) => ({
			id: c.categoryId ?? 'uncategorised',
			label: c.name,
			value: c.amountCents,
			color: c.color
		}))
	);
</script>

<svelte:head><title>Home · CouplesCash</title></svelte:head>

<AppBar title="{greeting}{firstName ? `, ${firstName}` : ''}" subtitle={formatMonth(today())}>
	{#snippet action()}
		<a href="/couple" class="btn btn-circle min-h-11 min-w-11 btn-ghost btn-sm" aria-label="Couple">
			<Icon name="heart-handshake" size={22} />
		</a>
	{/snippet}
</AppBar>

<main class="flex flex-col gap-4 px-4 pt-2">
	{#if !s}
		<NoHousehold />
	{:else if !hasAnything}
		<EmptyState
			icon="wallet"
			title="Let's add your first account"
			body="Start with a checking or savings account. Bills and subscriptions come next, and the dashboard fills itself in."
			cta="Add an account"
			href="/accounts/new"
		>
			<a href="/bills/new" class="btn mt-1 min-h-11 btn-ghost btn-sm">Or add a bill first</a>
		</EmptyState>
	{:else}
		<!-- Net worth hero -->
		<section
			class="card border-0 bg-gradient-to-br from-primary to-primary/80 text-primary-content shadow-md"
			aria-labelledby="networth"
		>
			<div class="card-body gap-3 p-5">
				<p
					id="networth"
					class="text-xs font-medium tracking-wide text-primary-content/80 uppercase"
				>
					Net worth
				</p>
				<p class="text-4xl leading-none font-bold tracking-tight">
					{formatMoney(s.netWorthCents, { whole: true })}
				</p>
				<div class="mt-1 grid grid-cols-2 gap-3 text-sm">
					<div class="rounded-xl bg-primary-content/15 px-3 py-2">
						<p class="text-xs text-primary-content/80">Assets</p>
						<p class="font-semibold tabular">{formatMoney(s.assetsCents, { whole: true })}</p>
					</div>
					<div class="rounded-xl bg-primary-content/15 px-3 py-2">
						<p class="text-xs text-primary-content/80">Owed</p>
						<p class="font-semibold tabular">{formatMoney(s.liabilitiesCents, { whole: true })}</p>
					</div>
				</div>
			</div>
		</section>

		<!-- This month -->
		<section class="card border border-base-300/60 bg-base-100 shadow-sm" aria-labelledby="month">
			<div class="card-body gap-3 p-5">
				<div class="flex items-baseline justify-between">
					<h2 id="month" class="font-semibold">This month</h2>
					<a href="/insights" class="link text-sm link-primary no-underline">Insights</a>
				</div>
				<div class="flex items-center gap-4">
					<div class="flex-1 space-y-2 text-sm">
						<div class="flex justify-between gap-3">
							<span class="text-base-content/65">Due</span>
							<MoneyText cents={s.dueThisMonthCents} class="font-medium" tone="none" />
						</div>
						<div class="flex justify-between gap-3">
							<span class="text-base-content/65">Paid</span>
							<MoneyText cents={s.paidThisMonthCents} class="font-medium" tone="none" />
						</div>
						<div class="flex justify-between gap-3">
							<span class="text-base-content/65">Remaining</span>
							<MoneyText cents={s.remainingThisMonthCents} class="font-semibold" tone="none" />
						</div>
						<progress
							class="progress h-2 w-full progress-secondary"
							value={paidPct}
							max="100"
							aria-label="{paidPct}% of this month's bills paid"
						></progress>
						<p class="text-xs text-base-content/60">{paidPct}% paid</p>
					</div>
					{#if donutSlices.length}
						<Donut
							slices={donutSlices}
							size={120}
							thickness={14}
							centerLabel="/mo"
							maxSlices={5}
							legend={false}
						/>
					{/if}
				</div>
			</div>
		</section>

		<!-- Upcoming -->
		<section
			class="card border border-base-300/60 bg-base-100 shadow-sm"
			aria-labelledby="upcoming"
		>
			<div class="card-body gap-1 p-5">
				<div class="flex items-baseline justify-between">
					<h2 id="upcoming" class="font-semibold">Due in the next 14 days</h2>
					<a href="/calendar" class="link text-sm link-primary no-underline">Calendar</a>
				</div>
				{#if upcoming.length === 0}
					<p class="py-4 text-sm text-base-content/60">Nothing due soon. Enjoy the quiet.</p>
				{:else}
					<ul class="divide-y divide-base-300/60">
						{#each upcoming as occ (occurrenceKey(occ))}
							<OccurrenceRow {occ} {onToggle} busy={busy.includes(occurrenceKey(occ))} />
						{/each}
					</ul>
				{/if}
			</div>
		</section>
	{/if}
</main>

{#if s}
	<!-- Quick add -->
	<div class="fab fixed right-4 bottom-[calc(5rem+env(safe-area-inset-bottom))] z-30">
		<div
			tabindex="0"
			role="button"
			class="btn btn-circle shadow-lg btn-lg btn-primary"
			aria-label="Quick add"
		>
			<Icon name="plus" size={26} strokeWidth={2.5} />
		</div>
		<a href="/bills/new" class="btn rounded-full shadow-md btn-lg">
			<Icon name="receipt" size={18} /> Bill or subscription
		</a>
		<a href="/accounts/new" class="btn rounded-full shadow-md btn-lg">
			<Icon name="wallet" size={18} /> Account
		</a>
	</div>
{/if}
