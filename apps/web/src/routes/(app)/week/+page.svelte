<script lang="ts">
	import { enhance } from '$app/forms';
	import { invalidateAll } from '$app/navigation';
	import type { BillPayment, Expense, InboxItem, Occurrence, WeekView } from '#lib/types.ts';
	import TransactionSheet from '#lib/components/TransactionSheet.svelte';
	import { apiFetch } from '#lib/utils/client.ts';
	import { itemFromPayment } from '#lib/utils/transactions.ts';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import NoHousehold from '#lib/components/NoHousehold.svelte';
	import OccurrenceRow from '#lib/components/OccurrenceRow.svelte';
	import BottomSheet from '#lib/components/BottomSheet.svelte';
	import OneOffForm from '#lib/components/OneOffForm.svelte';
	import WeekBudgetBars from '#lib/components/WeekBudgetBars.svelte';
	import { centsToInput, currencySymbol, formatMoney } from '#lib/utils/money.ts';
	import { addDays, formatDate, formatWeekdayDate, today } from '#lib/utils/dates.ts';
	import { firstFutureDay, weekRangeLabel } from '#lib/utils/week.ts';
	import { occurrenceKey, optimisticToggle, togglePaid } from '#lib/utils/occurrences.ts';
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
			void invalidateAll(); // recompute left-at-end and the budget bars
		} catch {
			view = previous;
			toasts.show('Could not update. Check your connection.', 'error');
		} finally {
			busy = null;
		}
	}

	// --- sheets (one-off + weekly budgets), driven by form actions
	let sheet = $state<'none' | 'oneoff' | 'budgets'>('none');
	let open = $derived(sheet !== 'none');
	let submitting = $state(false);
	$effect(() => {
		if (form?.oneOffAdded) {
			toasts.show(`${form.oneOffAdded} planned`, 'success');
			sheet = 'none';
		}
		if (form?.budgetsSaved) {
			toasts.show('Weekly budgets saved', 'success');
			sheet = 'none';
		}
	});
	$effect(() => {
		if (!open) sheet = 'none';
	});

	const subtitle = $derived(
		!view ? '' : view.isPast ? 'actual' : view.isCurrent ? 'if nothing else happens' : 'projected'
	);
	const plannedCount = $derived(view ? view.days.reduce((s, d) => s + d.planned.length, 0) : 0);
	const defaultDate = $derived(view ? firstFutureDay(view.days, todayIso) : todayIso);
	const incomeToCome = $derived(
		view ? Math.max(0, view.incomeExpectedCents - view.incomeReceivedCents) : 0
	);
	const categoryById = $derived(new Map(data.categories.map((c) => [c.id, c])));
	const weeklyByCategory = $derived(
		new Map((view?.budget.categories ?? []).map((r) => [r.categoryId, r.budgetCents]))
	);
	const dayLabel = (iso: string) => formatWeekdayDate(iso);
	// v5: spendable is the bank's available balance when a linked checking account reports one
	const hasAvailable = $derived(
		data.accounts.some((a) => a.type === 'checking' && a.availableCents != null)
	);

	// v5: a bill paid from the bank opens the shared transaction sheet (unmatch, match elsewhere…)
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
	$effect(() => {
		if (!txOpen && txDirty) {
			txDirty = false;
			void invalidateAll();
		}
	});
	const expenseCategory = (e: Expense) =>
		e.categoryId ? categoryById.get(e.categoryId) : undefined;
</script>

<svelte:head><title>This week · CouplesCash</title></svelte:head>

<AppBar
	title="This week"
	subtitle={view ? weekRangeLabel(view.start, view.end) : undefined}
	back="/"
>
	{#snippet action()}
		{#if view}
			<button
				type="button"
				class="btn btn-circle min-h-11 min-w-11 btn-ghost btn-sm"
				aria-label="Plan a one-off"
				data-testid="plan-one-off"
				onclick={() => (sheet = 'oneoff')}
			>
				<Icon name="plus" size={22} />
			</button>
		{/if}
	{/snippet}
</AppBar>

<main class="flex flex-col gap-4 px-4 pt-2">
	{#if !data.household}
		<NoHousehold />
	{:else if !view}
		<p class="py-6 text-center text-sm text-base-content/60">Could not load this week.</p>
	{:else}
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
				{#if view.isCurrent}
					<span class="badge badge-sm badge-secondary">now</span>
				{:else}
					<a href="/week" class="link text-xs link-primary no-underline" data-testid="week-today"
						>Today</a
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

		<!-- Hero -->
		<section
			class="card border-0 bg-gradient-to-br from-primary to-primary/80 text-primary-content shadow-md"
			aria-labelledby="left"
		>
			<div class="card-body gap-3 p-5">
				<p id="left" class="text-xs font-medium tracking-wide text-primary-content/80 uppercase">
					Left at end of week
				</p>
				{#if view.leftAtEndCents === null}
					<p class="text-lg font-semibold" data-testid="left-at-end">
						No balance history for this week
					</p>
				{:else}
					<p
						class="text-4xl leading-none font-bold tracking-tight tabular"
						data-testid="left-at-end"
					>
						{formatMoney(view.leftAtEndCents)}
					</p>
					<p class="text-xs text-primary-content/80" data-testid="left-subtitle">{subtitle}</p>
				{/if}
				<div class="mt-1 grid grid-cols-2 gap-2 text-sm">
					<div class="rounded-xl bg-primary-content/15 px-3 py-2">
						<p class="text-xs text-primary-content/80" data-testid="spendable-label">
							{hasAvailable ? 'Available now' : 'Spendable now'}
						</p>
						<p class="font-semibold tabular" data-testid="stat-spendable">
							{formatMoney(view.spendableNowCents)}
						</p>
					</div>
					<div class="rounded-xl bg-primary-content/15 px-3 py-2">
						<p class="text-xs text-primary-content/80">Income to come</p>
						<p class="font-semibold tabular" data-testid="stat-income">
							{formatMoney(incomeToCome)}
						</p>
					</div>
					<div class="rounded-xl bg-primary-content/15 px-3 py-2">
						<p class="text-xs text-primary-content/80">Planned to pay</p>
						<p class="font-semibold tabular" data-testid="stat-planned">
							{formatMoney(view.remainingOutCents)}
						</p>
					</div>
					<div class="rounded-xl bg-primary-content/15 px-3 py-2">
						<p class="text-xs text-primary-content/80">Unplanned so far</p>
						<p class="font-semibold tabular" data-testid="stat-unplanned">
							{formatMoney(view.unplannedCents)}
						</p>
					</div>
				</div>
			</div>
		</section>

		<!-- Overdue -->
		{#if view.overdue.length > 0}
			<section
				class="card border border-error/40 bg-error/5 shadow-sm"
				aria-labelledby="overdue"
				data-testid="overdue-section"
			>
				<div class="card-body gap-1 p-5">
					<h2 id="overdue" class="flex items-center gap-2 font-semibold text-error">
						<Icon name="circle-alert" size={18} /> Overdue
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
						{plannedCount} planned · {formatMoney(view.plannedOutCents)} out
					</span>
				</div>
				{#if plannedCount === 0 && view.unplannedCents === 0}
					<p class="py-3 text-sm text-base-content/60">
						Nothing planned this week yet. Tap <strong>+</strong> to plan a one-off.
					</p>
				{/if}
				<ol class="flex flex-col gap-3">
					{#each view.days as day (day.date)}
						{@const isToday = day.date === todayIso}
						<li data-testid="week-day" data-date={day.date}>
							<div class="flex items-center gap-2 pb-1">
								<h3
									class="text-xs font-semibold tracking-wide uppercase {isToday
										? 'text-primary'
										: 'text-base-content/60'}"
								>
									{dayLabel(day.date)}
								</h3>
								{#if isToday}
									<span class="badge badge-xs badge-primary">Today</span>
								{/if}
								<span class="flex-1"></span>
								{#if day.plannedOutCents > 0 || day.plannedInCents > 0}
									<span class="text-xs text-base-content/50 tabular">
										{#if day.plannedInCents > 0}<span class="text-success"
												>+{formatMoney(day.plannedInCents)}</span
											>{/if}
										{#if day.plannedInCents > 0 && day.plannedOutCents > 0}<span aria-hidden="true">
												·
											</span>{/if}
										{#if day.plannedOutCents > 0}{formatMoney(day.plannedOutCents)}{/if}
									</span>
								{/if}
							</div>
							{#if day.planned.length === 0 && day.expenses.length === 0}
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
											</a>
											<span class="text-base-content/70 tabular">{formatMoney(e.amountCents)}</span>
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
						Expenses plus paid bills this week, against your weekly target and per-category amounts
					</p>
				</div>
				<WeekBudgetBars
					budget={view.budget}
					currentStart={view.start}
					onEdit={() => (sheet = 'budgets')}
				/>
			</div>
		</section>
	{/if}
</main>

{#if view}
	<BottomSheet bind:open title={sheet === 'budgets' ? 'Set weekly budgets' : 'Plan a one-off'}>
		{#if sheet === 'oneoff'}
			<form
				method="POST"
				action="?/planOneOff"
				class="flex flex-col gap-3"
				use:enhance={() => {
					submitting = true;
					return async ({ update }) => {
						await update();
						submitting = false;
					};
				}}
			>
				<OneOffForm
					accounts={data.accounts}
					defaultAccountId={data.household?.defaultAccountId ?? null}
					{defaultDate}
					values={form?.sheet === 'oneoff' ? form.values : undefined}
					error={form?.sheet === 'oneoff' ? form.error : undefined}
				/>
				<button class="btn min-h-12 rounded-full btn-primary" disabled={submitting}>
					{#if submitting}<span class="loading loading-sm loading-spinner"></span>{/if}
					Plan it
				</button>
			</form>
		{:else if sheet === 'budgets'}
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
				{#if form?.sheet === 'budgets' && form.error}
					<div class="alert text-sm alert-error" role="alert">{form.error}</div>
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
							defaultValue={form?.sheet === 'budgets'
								? (form.values?.overall ?? '')
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
									defaultValue={form?.sheet === 'budgets'
										? (form.values?.[`cat_${c.id}`] ?? '')
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
		{/if}
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

{#if view && !view.isCurrent}
	<p class="sr-only">Viewing {formatDate(view.start)} to {formatDate(view.end)}</p>
{/if}
