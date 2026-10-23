<script lang="ts">
	import { replaceState } from '$app/navigation';
	import type { CalendarResponse, Occurrence } from '#lib/types.ts';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import NoHousehold from '#lib/components/NoHousehold.svelte';
	import OccurrenceRow from '#lib/components/OccurrenceRow.svelte';
	import { apiFetch } from '#lib/utils/client.ts';
	import { formatMoney } from '#lib/utils/money.ts';
	import {
		addMonths,
		daysInMonth,
		endOfMonth,
		formatMonth,
		formatWeekdayDate,
		parseISODate,
		startOfMonth,
		today,
		weekday
	} from '#lib/utils/dates.ts';
	import { occurrenceKey, optimisticToggle, togglePaid } from '#lib/utils/occurrences.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data }: PageProps = $props();

	// Writable deriveds: reset from server data on invalidation, overridden by client month nav
	let month = $derived(data.month);
	let occurrences = $derived<Occurrence[]>(data.occurrences ?? []);
	let loading = $state(false);
	let selected = $state<string | null>(null);
	let busy = $state<string | null>(null);
	let drawer = $state<HTMLDivElement | null>(null);

	const todayIso = today();
	const first = $derived(`${month}-01`);
	const total = $derived(
		daysInMonth(parseISODate(first).getFullYear(), parseISODate(first).getMonth())
	);
	const offset = $derived(weekday(first));
	const cells = $derived.by(() => {
		const list: (string | null)[] = Array.from({ length: offset }, () => null);
		for (let d = 1; d <= total; d++) list.push(`${month}-${d < 10 ? `0${d}` : d}`);
		while (list.length % 7) list.push(null);
		return list;
	});
	const byDay = $derived.by(() => {
		const map: Record<string, Occurrence[]> = {};
		for (const o of occurrences) (map[o.dueDate] ??= []).push(o);
		return map;
	});
	const monthOut = $derived(
		occurrences.filter((o) => o.kind !== 'income').reduce((s, o) => s + o.amountCents, 0)
	);
	const monthPaid = $derived(
		occurrences.filter((o) => o.kind !== 'income' && o.paid).reduce((s, o) => s + o.amountCents, 0)
	);
	const dayItems = $derived(selected ? (byDay[selected] ?? []) : []);
	const weekdays = ['S', 'M', 'T', 'W', 'T', 'F', 'S'];

	async function go(delta: number) {
		const next = addMonths(first, delta).slice(0, 7);
		const prev = month;
		month = next;
		selected = null;
		loading = true;
		try {
			const res = await apiFetch<CalendarResponse>(
				`/calendar?from=${startOfMonth(`${next}-01`)}&to=${endOfMonth(`${next}-01`)}`
			);
			occurrences = res.occurrences;
			replaceState(`?m=${next}`, {});
		} catch {
			month = prev;
			toasts.show('Could not load that month.', 'error');
		} finally {
			loading = false;
		}
	}

	async function onToggle(occ: Occurrence) {
		const key = occurrenceKey(occ);
		const i = occurrences.findIndex((o) => occurrenceKey(o) === key);
		if (i < 0 || busy) return;
		const previous = occurrences[i];
		occurrences = occurrences.with(i, optimisticToggle(occ, data.user?.id ?? ''));
		busy = key;
		try {
			occurrences = occurrences.with(i, await togglePaid(occ));
		} catch {
			occurrences = occurrences.with(i, previous);
			toasts.show('Could not update.', 'error');
		} finally {
			busy = null;
		}
	}

	// Swipe between months
	let startX = 0;
	let startY = 0;
	function onPointerDown(e: PointerEvent) {
		startX = e.clientX;
		startY = e.clientY;
	}
	function onPointerUp(e: PointerEvent) {
		const dx = e.clientX - startX;
		const dy = e.clientY - startY;
		if (Math.abs(dx) > 60 && Math.abs(dx) > Math.abs(dy) * 1.5) void go(dx < 0 ? 1 : -1);
	}

	function select(day: string) {
		selected = selected === day ? null : day;
		if (selected) queueMicrotask(() => drawer?.focus());
	}
	function onKey(e: KeyboardEvent) {
		if (e.key === 'Escape') selected = null;
	}
</script>

<svelte:head><title>Calendar · CouplesCash</title></svelte:head>
<svelte:window onkeydown={onKey} />

<AppBar
	title="Calendar"
	subtitle={data.occurrences
		? `${formatMoney(monthPaid)} of ${formatMoney(monthOut)} paid`
		: undefined}
>
	{#snippet action()}
		{#if data.occurrences}
			<a
				href="/bills/new"
				class="btn btn-circle min-h-11 min-w-11 btn-ghost btn-sm"
				aria-label="Add bill"
			>
				<Icon name="plus" size={22} />
			</a>
		{/if}
	{/snippet}
</AppBar>

<main class="flex flex-col gap-4 px-4 pt-2">
	{#if !data.occurrences}
		<NoHousehold />
	{:else}
		<section class="card border border-base-300/60 bg-base-100 shadow-sm">
			<div class="card-body gap-3 p-4">
				<div class="flex items-center justify-between">
					<button
						type="button"
						class="btn btn-circle min-h-11 min-w-11 btn-ghost"
						aria-label="Previous month"
						onclick={() => go(-1)}
					>
						<Icon name="chevron-left" size={22} />
					</button>
					<h2 class="text-base font-semibold" aria-live="polite" data-testid="month-label">
						{formatMonth(first)}
						{#if loading}<span class="loading ml-2 loading-xs loading-spinner align-middle"
							></span>{/if}
					</h2>
					<button
						type="button"
						class="btn btn-circle min-h-11 min-w-11 btn-ghost"
						aria-label="Next month"
						onclick={() => go(1)}
					>
						<Icon name="chevron-right" size={22} />
					</button>
				</div>

				<div class="grid grid-cols-7 text-center" aria-hidden="true">
					{#each weekdays as w, i (i)}
						<span class="pb-1 text-xs font-medium text-base-content/50">{w}</span>
					{/each}
				</div>

				<div
					class="grid grid-cols-7 gap-y-1 transition-opacity {loading ? 'opacity-60' : ''}"
					role="region"
					aria-label="{formatMonth(first)} calendar"
					onpointerdown={onPointerDown}
					onpointerup={onPointerUp}
				>
					{#each cells as day, i (i)}
						{#if day}
							{@const items = byDay[day] ?? []}
							{@const isToday = day === todayIso}
							{@const unpaid = items.filter((o) => !o.paid && o.kind !== 'income').length}
							<button
								type="button"
								data-date={day}
								aria-label="{formatWeekdayDate(day)}{items.length
									? `, ${items.length} item${items.length === 1 ? '' : 's'}`
									: ''}"
								aria-pressed={selected === day}
								class="relative flex h-12 flex-col items-center justify-start rounded-xl pt-1.5 text-sm transition-colors {selected ===
								day
									? 'bg-primary text-primary-content'
									: isToday
										? 'bg-base-300/60 font-semibold'
										: 'hover:bg-base-200'}"
								onclick={() => select(day)}
							>
								<span class="leading-none {isToday && selected !== day ? 'text-primary' : ''}"
									>{Number(day.slice(8))}</span
								>
								{#if items.length}
									<span class="mt-1.5 flex items-center gap-0.5" aria-hidden="true">
										{#each items.slice(0, 3) as o (occurrenceKey(o))}
											<span
												class="h-1.5 w-1.5 rounded-full {o.paid ? 'opacity-40' : ''}"
												style:background={selected === day
													? 'currentColor'
													: (o.categoryColor ?? 'var(--color-neutral)')}
											></span>
										{/each}
										{#if items.length > 3}<span class="text-[0.55rem] leading-none"
												>+{items.length - 3}</span
											>{/if}
									</span>
								{/if}
								{#if unpaid && day < todayIso && selected !== day}
									<span
										class="absolute top-1 right-1 h-1.5 w-1.5 rounded-full bg-error"
										aria-hidden="true"
									></span>
								{/if}
							</button>
						{:else}
							<span aria-hidden="true"></span>
						{/if}
					{/each}
				</div>
			</div>
		</section>

		{#if occurrences.length === 0}
			<p class="py-2 text-center text-sm text-base-content/60">
				Nothing due in {formatMonth(first)}.
			</p>
		{:else if !selected}
			<p class="py-2 text-center text-sm text-base-content/60">Tap a day to see what's due.</p>
		{/if}
	{/if}
</main>

{#if selected}
	<!-- Day drawer -->
	<button
		type="button"
		class="fixed inset-0 z-30 bg-black/30 backdrop-blur-[1px]"
		aria-label="Close"
		onclick={() => (selected = null)}
	></button>
	<div
		bind:this={drawer}
		tabindex="-1"
		role="dialog"
		aria-modal="true"
		aria-labelledby="drawer-title"
		class="fixed inset-x-0 bottom-0 z-40 mx-auto max-w-xl rounded-t-3xl bg-base-100 safe-bottom shadow-2xl outline-none motion-safe:animate-[slideUp_220ms_ease-out]"
	>
		<div class="mx-auto mt-2 h-1 w-10 rounded-full bg-base-300"></div>
		<div class="flex items-center justify-between px-5 pt-3">
			<h2 id="drawer-title" class="font-semibold">{formatWeekdayDate(selected)}</h2>
			<button
				type="button"
				class="btn btn-circle min-h-11 min-w-11 btn-ghost btn-sm"
				aria-label="Close"
				onclick={() => (selected = null)}
			>
				<Icon name="x" size={20} />
			</button>
		</div>
		<div class="max-h-[55dvh] overflow-y-auto px-5 pb-4">
			{#if dayItems.length === 0}
				<p class="py-6 text-center text-sm text-base-content/60">Nothing due this day.</p>
				<a
					href="/bills/new?anchor={selected}"
					class="btn mb-2 min-h-11 w-full rounded-full btn-ghost">Add something</a
				>
			{:else}
				<ul class="divide-y divide-base-300/60">
					{#each dayItems as occ (occurrenceKey(occ))}
						<OccurrenceRow {occ} {onToggle} showDate={false} busy={busy === occurrenceKey(occ)} />
					{/each}
				</ul>
			{/if}
		</div>
	</div>
{/if}

<style>
	@keyframes -global-slideUp {
		from {
			transform: translateY(24px);
			opacity: 0;
		}
		to {
			transform: none;
			opacity: 1;
		}
	}
</style>
