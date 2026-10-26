<script lang="ts">
	import { untrack } from 'svelte';
	import { enhance } from '$app/forms';
	import { invalidateAll } from '$app/navigation';
	import type { BillPayment, Expense, InboxItem, Occurrence, WeekView } from '#lib/types.ts';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Amount from '#lib/components/Amount.svelte';
	import BottomSheet from '#lib/components/BottomSheet.svelte';
	import FirstRun from '#lib/components/FirstRun.svelte';
	import FoundBills from '#lib/components/FoundBills.svelte';
	import HelperText from '#lib/components/HelperText.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import NoHousehold from '#lib/components/NoHousehold.svelte';
	import OccurrenceRow from '#lib/components/OccurrenceRow.svelte';
	import QuickAddSheet, { quickAddForm } from '#lib/components/QuickAddSheet.svelte';
	import TransactionSheet from '#lib/components/TransactionSheet.svelte';
	import WeekBudgetBars from '#lib/components/WeekBudgetBars.svelte';
	import { apiFetch } from '#lib/utils/client.ts';
	import { addDays, formatWeekdayDate, today } from '#lib/utils/dates.ts';
	import { centsToInput, currencySymbol, formatMoney, signedMoney } from '#lib/utils/money.ts';
	import { occurrenceKey, optimisticToggle, togglePaid } from '#lib/utils/occurrences.ts';
	import { itemFromPayment } from '#lib/utils/transactions.ts';
	import { weekRangeLabel } from '#lib/utils/week.ts';
	import { review } from '#lib/state/review.svelte.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data, form }: PageProps = $props();

	// Writable derived: resets when the server data changes, patched locally for optimistic toggles
	let view = $derived<WeekView | null>(data.view);
	let busy = $state<string | null>(null);
	const todayIso = today();

	function patch(v: WeekView, key: string, next: Occurrence): WeekView {
		const swap = (list: Occurrence[]) => list.map((o) => (occurrenceKey(o) === key ? next : o));
		return {
			...v,
			overdue: swap(v.overdue),
			days: v.days.map((d) => ({ ...d, planned: swap(d.planned) }))
		};
	}

	async function onToggle(occ: Occurrence) {
		if (!view || busy) return;
		const key = occurrenceKey(occ);
		const previous = view;
		const wasAuto = !!occ.paid?.auto;
		view = patch(view, key, optimisticToggle(occ, data.user?.id ?? ''));
		busy = key;
		try {
			const saved = await togglePaid(occ);
			view = patch(view, key, saved);
			toasts.show(
				wasAuto
					? `${occ.name} is unpaid again`
					: saved.paid
						? `${occ.name} marked paid`
						: `${occ.name} marked unpaid`,
				'success'
			);
			void invalidateAll(); // recompute what's left and the budget bars
		} catch {
			view = previous;
			toasts.show('Could not update. Check your connection.', 'error');
		} finally {
			busy = null;
		}
	}

	// --- quick add (+) and the weekly-budgets sheet
	let quickOpen = $state(false);
	let budgetsOpen = $state(false);
	let submitting = $state(false);
	const quick = $derived(quickAddForm(form));
	$effect(() => {
		if (form && 'budgetsSaved' in form && form.budgetsSaved) {
			toasts.show('Weekly budgets saved', 'success');
			budgetsOpen = false;
		}
	});
	$effect(() => {
		if (data.joined) untrack(() => toasts.show('You are in! Welcome to the household.', 'success'));
	});
	const budgetError = $derived(form && 'sheet' in form && form.sheet === 'budgets' ? form : null);

	// --- first run: three steps, each ticked by real data
	const hasAccounts = $derived(data.accounts.length > 0 || (data.inbox?.linkCount ?? 0) > 0);
	const hasBills = $derived(data.itemCount > 0);
	const hasPartner = $derived((data.household?.members.length ?? 0) >= 2);
	const firstRun = $derived(!hasAccounts && !hasBills);
	const setupLeft = $derived(!(hasAccounts && hasBills && hasPartner));

	// --- pending: rows still in flight, by what they paid (planned item / expense) or by day
	const pendingItems = $derived(data.pending?.groups.flatMap((g) => g.items) ?? []);
	const pendingOcc = $derived(
		new Set(
			pendingItems.filter((i) => i.match).map((i) => `${i.match!.itemId}:${i.match!.dueDate}`)
		)
	);
	const pendingExpense = $derived(
		new Set(pendingItems.map((i) => i.expense?.expenseId).filter(Boolean) as string[])
	);
	const pendingByDay = $derived.by(() => {
		const byDay: Record<string, InboxItem[]> = {};
		for (const i of pendingItems) {
			if (i.match || i.expense || i.status === 'ignored') continue;
			(byDay[i.postedOn] ??= []).push(i);
		}
		return byDay;
	});

	const leftLabel = $derived(
		!view
			? ''
			: view.isPast
				? 'Left at the end'
				: view.isCurrent
					? 'Left this week'
					: 'Projected left'
	);
	const subtitle = $derived(
		!view ? '' : view.isPast ? 'actual' : view.isCurrent ? 'if nothing else happens' : 'projected'
	);
	const plannedCount = $derived(view ? view.days.reduce((s, d) => s + d.planned.length, 0) : 0);
	const incomeToCome = $derived(
		view ? Math.max(0, view.incomeExpectedCents - view.incomeReceivedCents) : 0
	);
	const pendingOut = $derived(view?.pendingOutCents ?? data.pending?.totalOutCents ?? 0);
	const categoryById = $derived(new Map(data.categories.map((c) => [c.id, c])));
	const weeklyByCategory = $derived(
		new Map((view?.budget.categories ?? []).map((r) => [r.categoryId, r.budgetCents]))
	);
	const hasAvailable = $derived(
		data.accounts.some((a) => a.type === 'checking' && a.availableCents != null)
	);
	const toReview = $derived(review.count);
	const expenseCategory = (e: Expense) =>
		e.categoryId ? categoryById.get(e.categoryId) : undefined;

	// v5: a bill paid from the bank (and any pending row) opens the shared transaction sheet
	let txOpen = $state(false);
	let txItem = $state<InboxItem | null>(null);
	let txDirty = $state(false);
	async function openBank(occ: Occurrence) {
		try {
			const list = await apiFetch<BillPayment[]>(`/recurring/${occ.itemId}/payments?limit=12`);
			const p = list.find((x) => x.dueDate === occ.dueDate);
			const row = p ? itemFromPayment({ id: occ.itemId, name: occ.name, kind: occ.kind }, p) : null;
			if (!row) {
				toasts.show('Could not find the bank transaction.', 'error');
				return;
			}
			txItem = row;
			txOpen = true;
		} catch {
			toasts.show('Could not load the bank transaction.', 'error');
		}
	}
	function openTx(item: InboxItem) {
		txItem = item;
		txOpen = true;
	}
	$effect(() => {
		if (!txOpen && txDirty) {
			txDirty = false;
			void invalidateAll();
		}
	});
</script>

<svelte:head><title>This week · CouplesCash</title></svelte:head>

<AppBar title="This week" subtitle={view ? weekRangeLabel(view.start, view.end) : undefined}>
	{#snippet action()}
		{#if data.household}
			<button
				type="button"
				class="btn btn-circle min-h-11 min-w-11 btn-primary btn-sm"
				aria-label="Add something"
				data-testid="quick-add"
				onclick={() => (quickOpen = true)}
			>
				<Icon name="plus" size={22} strokeWidth={2.5} />
			</button>
		{/if}
	{/snippet}
</AppBar>

<main class="flex flex-col gap-4 px-4 pt-2">
	<HelperText id="week" text="How much is left this week, and what's coming." />
	{#if !data.household}
		<NoHousehold />
	{:else if !view}
		<p class="py-6 text-center text-sm text-base-content/60">Could not load this week.</p>
	{:else if firstRun}
		<FirstRun {hasAccounts} {hasBills} {hasPartner} onaddbill={() => (quickOpen = true)} />
	{:else}
		{#if setupLeft}
			<FirstRun {hasAccounts} {hasBills} {hasPartner} onaddbill={() => (quickOpen = true)} />
		{/if}

		<!-- To review -->
		{#if toReview > 0}
			<a
				href="/transactions"
				class="flex min-h-14 items-center gap-3 rounded-2xl bg-primary/10 px-4 py-3 text-sm"
				data-testid="review-banner"
			>
				<span
					class="flex h-9 w-9 shrink-0 items-center justify-center rounded-xl bg-primary text-primary-content"
				>
					<Icon name="inbox" size={18} />
				</span>
				<span class="flex-1 font-semibold">{toReview} to review</span>
				<span class="text-xs text-base-content/60">Mostly one tap each</span>
				<Icon name="chevron-right" size={18} class="text-base-content/40" />
			</a>
		{:else if (data.inbox?.linkCount ?? 0) > 0}
			<p
				class="flex items-center gap-2 px-1 text-sm text-base-content/70"
				data-testid="all-caught-up"
			>
				<Icon name="circle-check" size={18} class="text-success" /> All caught up
			</p>
		{/if}

		<!-- Week navigation -->
		<div class="flex items-center justify-between">
			<a
				href="?start={addDays(view.start, -7)}"
				class="btn btn-circle min-h-11 min-w-11 btn-ghost"
				aria-label="Previous week"
			>
				<Icon name="chevron-left" size={22} />
			</a>
			<div class="text-center">
				<h2 class="text-sm font-semibold" data-testid="week-label">
					{weekRangeLabel(view.start, view.end)}
				</h2>
				{#if !view.isCurrent}
					<a href="/week" class="link text-xs link-primary no-underline" data-testid="week-today"
						>Back to this week</a
					>
				{/if}
			</div>
			<a
				href="?start={addDays(view.start, 7)}"
				class="btn btn-circle min-h-11 min-w-11 btn-ghost"
				aria-label="Next week"
			>
				<Icon name="chevron-right" size={22} />
			</a>
		</div>

		<!-- Hero: one number -->
		<section
			class="card border-0 bg-gradient-to-br from-primary to-primary/80 text-primary-content shadow-md"
			aria-labelledby="left"
		>
			<div class="card-body gap-3 p-5">
				<p id="left" class="text-sm font-medium text-primary-content/85">{leftLabel}</p>
				{#if view.leftAtEndCents === null}
					<p class="text-lg font-semibold" data-testid="left-at-end">
						No balance history for this week
					</p>
				{:else}
					<p class="text-5xl leading-none font-bold tracking-tight" data-testid="left-at-end">
						{formatMoney(view.leftAtEndCents)}
					</p>
					<p class="text-xs text-primary-content/80" data-testid="left-subtitle">{subtitle}</p>
				{/if}
				{#if data.payday}
					<p
						class="flex items-center gap-1.5 text-sm text-primary-content/90"
						data-testid="next-payday"
					>
						<Icon name="calendar-clock" size={16} />
						Next payday {formatWeekdayDate(data.payday.date)}{#if data.payday.amountCents > 0}
							· {signedMoney(data.payday.amountCents)}{/if}
					</p>
				{/if}
				<dl class="mt-1 grid grid-cols-2 gap-2 text-sm">
					<div class="rounded-xl bg-primary-content/15 px-3 py-2">
						<dt class="text-xs text-primary-content/80" data-testid="spendable-label">
							{hasAvailable ? 'Available now' : 'Spendable now'}
						</dt>
						<dd class="font-semibold tabular" data-testid="stat-spendable">
							{formatMoney(view.spendableNowCents)}
						</dd>
					</div>
					<div class="rounded-xl bg-primary-content/15 px-3 py-2">
						<dt class="text-xs text-primary-content/80">Pending</dt>
						<dd class="font-semibold tabular" data-testid="stat-pending">
							{signedMoney(-pendingOut)}
						</dd>
					</div>
					<div class="rounded-xl bg-primary-content/15 px-3 py-2">
						<dt class="text-xs text-primary-content/80">Still to pay</dt>
						<dd class="font-semibold tabular" data-testid="stat-planned">
							{signedMoney(-view.remainingOutCents)}
						</dd>
					</div>
					<div class="rounded-xl bg-primary-content/15 px-3 py-2">
						<dt class="text-xs text-primary-content/80">Income to come</dt>
						<dd class="font-semibold tabular" data-testid="stat-income">
							{signedMoney(incomeToCome)}
						</dd>
					</div>
				</dl>
			</div>
		</section>

		<FoundBills suggestions={data.suggestions} />

		<!-- Late -->
		{#if view.overdue.length > 0}
			<section
				class="card border border-error/40 bg-error/5 shadow-sm"
				aria-labelledby="late"
				data-testid="overdue-section"
			>
				<div class="card-body gap-1 p-5">
					<h2 id="late" class="flex items-center gap-2 font-semibold text-error">
						<Icon name="circle-alert" size={18} /> Late
						<span class="badge badge-sm badge-error">{view.overdue.length}</span>
					</h2>
					<ul class="divide-y divide-error/20">
						{#each view.overdue as occ (occurrenceKey(occ))}
							<OccurrenceRow
								{occ}
								{onToggle}
								onBank={openBank}
								showStatus
								busy={busy === occurrenceKey(occ)}
							/>
						{/each}
					</ul>
				</div>
			</section>
		{/if}

		<!-- Day list -->
		<section class="card border border-base-300/60 bg-base-100 shadow-sm" aria-labelledby="days">
			<div class="card-body gap-2 p-5">
				<div class="flex items-baseline justify-between">
					<h2 id="days" class="font-semibold">Day by day</h2>
					<span class="text-xs text-base-content/60">
						{plannedCount} planned · {signedMoney(-view.plannedOutCents)}
					</span>
				</div>
				{#if plannedCount === 0 && view.unplannedCents === 0 && Object.keys(pendingByDay).length === 0}
					<p class="py-3 text-sm text-base-content/60">
						Nothing planned this week yet. Tap <strong>+</strong> and type something like
						<em>"oil change 60 friday"</em>.
					</p>
				{/if}
				<ol class="flex flex-col gap-3">
					{#each view.days as day (day.date)}
						{@const isToday = day.date === todayIso}
						{@const dayPending = pendingByDay[day.date] ?? []}
						<li data-testid="week-day" data-date={day.date}>
							<div class="flex items-center gap-2 pb-1">
								<h3
									class="text-xs font-semibold tracking-wide uppercase {isToday
										? 'text-primary'
										: 'text-base-content/60'}"
								>
									{formatWeekdayDate(day.date)}
								</h3>
								{#if isToday}
									<span class="badge badge-xs badge-primary">Today</span>
								{/if}
								<span class="flex-1"></span>
								{#if day.plannedInCents > 0}<Amount
										cents={day.plannedInCents}
										class="text-xs"
									/>{/if}
								{#if day.plannedOutCents > 0}<Amount
										cents={-day.plannedOutCents}
										class="text-xs"
									/>{/if}
							</div>
							{#if day.planned.length === 0 && day.expenses.length === 0 && dayPending.length === 0}
								<p class="border-l-2 border-base-300/60 py-1 pl-3 text-xs text-base-content/40">
									Nothing planned
								</p>
							{:else}
								<ul class="divide-y divide-base-300/60 border-l-2 border-base-300/60 pl-2">
									{#each day.planned as occ (occurrenceKey(occ))}
										<OccurrenceRow
											{occ}
											{onToggle}
											showDate={false}
											showStatus
											onBank={openBank}
											pending={pendingOcc.has(occurrenceKey(occ))}
											busy={busy === occurrenceKey(occ)}
										/>
									{/each}
									{#each day.expenses as e (e.id)}
										{@const cat = expenseCategory(e)}
										<li class="flex items-center gap-3 py-2 text-sm" data-testid="week-expense">
											<span
												class="ml-1.5 h-2 w-2 shrink-0 rounded-full"
												style:background={cat?.color ?? 'var(--color-base-300)'}
												aria-hidden="true"
											></span>
											<a
												href="/expenses/{e.id}"
												class="min-w-0 flex-1 truncate text-base-content/70"
											>
												{e.name}
												<span class="text-xs text-base-content/50">
													· {cat?.name ?? 'Unplanned'}</span
												>
												{#if pendingExpense.has(e.id)}
													<span
														class="badge border-0 bg-warning/20 px-1.5 badge-xs text-[0.65rem] text-pending"
														>Pending</span
													>
												{/if}
											</a>
											<Amount cents={-e.amountCents} />
										</li>
									{/each}
									{#each dayPending as t (t.id)}
										<li data-testid="week-pending">
											<button
												type="button"
												class="flex min-h-11 w-full items-center gap-3 py-2 text-left text-sm"
												onclick={() => openTx(t)}
											>
												<span
													class="ml-1.5 h-2 w-2 shrink-0 rounded-full bg-warning"
													aria-hidden="true"
												></span>
												<span class="min-w-0 flex-1 truncate text-base-content/70">
													{t.payee || t.description}
													<span
														class="badge border-0 bg-warning/20 px-1.5 badge-xs text-[0.65rem] text-pending"
														>Pending</span
													>
												</span>
												<Amount cents={t.amountCents} />
											</button>
										</li>
									{/each}
								</ul>
							{/if}
						</li>
					{/each}
				</ol>
			</div>
		</section>

		<!-- Weekly budget -->
		<section class="card border border-base-300/60 bg-base-100 shadow-sm" aria-labelledby="wbudget">
			<div class="card-body gap-3 p-5">
				<div>
					<h2 id="wbudget" class="font-semibold">Weekly budget</h2>
					<p class="text-xs text-base-content/60">
						Everything spent this week (expenses and paid bills) against your weekly target
					</p>
				</div>
				<WeekBudgetBars
					budget={view.budget}
					currentStart={view.start}
					onEdit={() => (budgetsOpen = true)}
				/>
			</div>
		</section>
	{/if}
</main>

{#if data.household}
	<QuickAddSheet
		bind:open={quickOpen}
		categories={data.categories}
		accounts={data.accounts}
		defaultAccountId={data.household.defaultAccountId}
		form={quick}
	/>
{/if}

{#if view}
	<BottomSheet bind:open={budgetsOpen} title="Set weekly budgets">
		<form
			method="POST"
			action="?/budgets"
			class="flex flex-col gap-3"
			use:enhance={() => {
				submitting = true;
				return async ({ update }) => {
					await update();
					submitting = false;
				};
			}}
		>
			{#if budgetError?.error}
				<div class="alert text-sm alert-error" role="alert">{budgetError.error}</div>
			{/if}
			<label class="form-control">
				<span class="label-text mb-1 text-sm font-medium">Overall weekly target</span>
				<div class="input-bordered input flex min-h-12 w-full items-center gap-2">
					<span class="text-base-content/50">{currencySymbol()}</span>
					<input
						name="overall"
						type="text"
						inputmode="decimal"
						class="grow tabular"
						placeholder="Blank to clear"
						aria-label="Overall weekly target"
						defaultValue={budgetError
							? (budgetError.values?.overall ?? '')
							: view.budget.overall
								? centsToInput(view.budget.overall.budgetCents)
								: ''}
					/>
				</div>
				<span class="label-text-alt mt-1 text-base-content/60">
					Everything you spend in a week, planned or not. Blank clears it.
				</span>
			</label>
			<p class="text-sm font-medium">
				Per category <span class="font-normal text-base-content/50">(blank = none)</span>
			</p>
			<ul class="flex flex-col gap-2">
				{#each data.categories.filter((c) => c.name !== 'Income') as c (c.id)}
					{@const current = weeklyByCategory.get(c.id)}
					<li class="flex items-center gap-2">
						<input type="hidden" name="categoryId" value={c.id} />
						<input type="hidden" name="had_{c.id}" value={current !== undefined ? '1' : '0'} />
						<span
							class="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg text-white"
							style:background={c.color}
						>
							<Icon name={c.icon} size={14} />
						</span>
						<span class="flex-1 truncate text-sm">{c.name}</span>
						<label class="input-bordered input flex min-h-11 w-32 items-center gap-1">
							<span class="text-base-content/50">{currencySymbol()}</span>
							<input
								name="cat_{c.id}"
								type="text"
								inputmode="decimal"
								class="w-full tabular"
								placeholder="—"
								aria-label="Weekly budget for {c.name}"
								defaultValue={budgetError
									? (budgetError.values?.[`cat_${c.id}`] ?? '')
									: current !== undefined
										? centsToInput(current)
										: ''}
							/>
						</label>
					</li>
				{/each}
			</ul>
			<button class="btn min-h-12 rounded-full btn-primary" disabled={submitting}>
				{#if submitting}<span class="loading loading-sm loading-spinner"></span>{/if}
				Save weekly budgets
			</button>
		</form>
	</BottomSheet>
{/if}

<TransactionSheet
	bind:open={txOpen}
	item={txItem}
	initial={null}
	categories={data.categories}
	onchange={(next) => {
		txItem = next;
		txDirty = true;
	}}
/>
