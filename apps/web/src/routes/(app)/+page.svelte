<script lang="ts">
	import { enhance } from '$app/forms';
	import { invalidateAll } from '$app/navigation';
	import type { Occurrence, Todo } from '#lib/types.ts';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import NoHousehold from '#lib/components/NoHousehold.svelte';
	import OccurrenceRow from '#lib/components/OccurrenceRow.svelte';
	import MoneyText from '#lib/components/MoneyText.svelte';
	import ActivityRow from '#lib/components/ActivityRow.svelte';
	import TodoRow from '#lib/components/TodoRow.svelte';
	import BottomSheet from '#lib/components/BottomSheet.svelte';
	import PaydayCard from '#lib/components/PaydayCard.svelte';
	import Donut from '#lib/charts/Donut.svelte';
	import { currencySymbol, formatMoney } from '#lib/utils/money.ts';
	import { formatMonth, today } from '#lib/utils/dates.ts';
	import { occurrenceKey, optimisticToggle, togglePaid } from '#lib/utils/occurrences.ts';
	import { apiFetch } from '#lib/utils/client.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data, form }: PageProps = $props();

	// Local, writable copy for optimistic "mark paid"; resets whenever the server data changes
	let upcoming = $derived<Occurrence[]>(data.summary?.upcoming ?? []);
	let busy = $state<string[]>([]);

	async function onToggle(occ: Occurrence) {
		const key = occurrenceKey(occ);
		const i = upcoming.findIndex((o) => occurrenceKey(o) === key);
		if (i < 0 || busy.includes(key)) return;
		const previous = upcoming[i];
		const wasAuto = !!occ.paid?.auto;
		upcoming = upcoming.with(i, optimisticToggle(occ, data.user?.id ?? ''));
		busy = [...busy, key];
		try {
			upcoming = upcoming.with(i, await togglePaid(occ));
			toasts.show(
				wasAuto
					? `${occ.name} is unpaid again`
					: upcoming[i].paid
						? `${occ.name} marked paid`
						: `${occ.name} marked unpaid`,
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

	// --- to-pay (optimistic through the proxy)
	let todos = $derived<Todo[]>(data.todos);
	let todoBusy = $state<string | null>(null);
	async function toggleTodo(todo: Todo) {
		if (todoBusy) return;
		const i = todos.findIndex((t) => t.id === todo.id);
		if (i < 0) return;
		const previous = todos[i];
		todos = todos.with(i, {
			...todo,
			doneAt: todo.doneAt ? null : new Date().toISOString(),
			doneByName: todo.doneAt ? null : (data.user?.name ?? null)
		});
		todoBusy = todo.id;
		try {
			// no invalidateAll: a just-ticked item stays visible (struck through) until the next visit
			await apiFetch<Todo>(`/todos/${todo.id}/done`, { method: todo.doneAt ? 'DELETE' : 'POST' });
		} catch {
			todos = todos.with(i, previous);
			toasts.show('Could not update.', 'error');
		} finally {
			todoBusy = null;
		}
	}

	// --- autopay banner: items auto-paid today, dismissible for the day
	const todayIso = today();
	let dismissed = $state(false);
	const autoPaidToday = $derived(upcoming.filter((o) => o.paid?.auto && o.dueDate === todayIso));
	$effect(() => {
		try {
			dismissed = sessionStorage.getItem(`cc-autopay-dismissed:${todayIso}`) === '1';
		} catch {
			dismissed = false;
		}
	});
	function dismissAutopay() {
		dismissed = true;
		try {
			sessionStorage.setItem(`cc-autopay-dismissed:${todayIso}`, '1');
		} catch {
			// ignore
		}
	}

	// --- quick add sheet
	let sheet = $state(false);
	let quickMode = $state<'menu' | 'expense'>('menu');
	let quickSubmitting = $state(false);
	function openSheet() {
		quickMode = 'menu';
		sheet = true;
	}
	$effect(() => {
		if (form?.expenseAdded) {
			toasts.show(`${form.expenseAdded} added`, 'success');
			sheet = false;
		}
	});

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
	// the first three items that were open when the page loaded, rendered from local state
	const shownTodoIds = $derived(
		data.todos
			.filter((t) => !t.doneAt)
			.slice(0, 3)
			.map((t) => t.id)
	);
	const openTodos = $derived(todos.filter((t) => shownTodoIds.includes(t.id)));
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
		{#if autoPaidToday.length && !dismissed}
			<div
				class="alert items-center border-secondary/30 bg-secondary/10 text-sm"
				role="status"
				data-testid="autopay-banner"
			>
				<Icon name="bot" size={20} class="text-secondary" />
				<span class="flex-1">
					{#each autoPaidToday.slice(0, 2) as o, i (occurrenceKey(o))}
						{#if i > 0}<span aria-hidden="true"> · </span>{/if}
						<strong>{o.name}</strong> was auto-paid · {formatMoney(
							o.paid?.amountCents ?? o.amountCents
						)}
					{/each}
					{#if autoPaidToday.length > 2}<span> and {autoPaidToday.length - 2} more</span>{/if}
				</span>
				{#if autoPaidToday.length === 1}
					<button
						type="button"
						class="btn min-h-11 rounded-full btn-ghost btn-sm"
						onclick={() => onToggle(autoPaidToday[0])}
					>
						Undo
					</button>
				{/if}
				<button
					type="button"
					class="btn btn-circle min-h-11 min-w-11 btn-ghost btn-sm"
					aria-label="Dismiss"
					onclick={dismissAutopay}
				>
					<Icon name="x" size={18} />
				</button>
			</div>
		{/if}

		<!-- Until payday -->
		<PaydayCard view={data.payperiod} />

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
				<p class="text-4xl leading-none font-bold tracking-tight" data-testid="hero-net-worth">
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

		<!-- To-pay -->
		{#if openTodos.length}
			<section class="card border border-base-300/60 bg-base-100 shadow-sm" aria-labelledby="topay">
				<div class="card-body gap-1 p-5">
					<div class="flex items-baseline justify-between">
						<h2 id="topay" class="font-semibold">To pay</h2>
						<a href="/todos" class="link text-sm link-primary no-underline">All items</a>
					</div>
					<ul class="divide-y divide-base-300/60" data-testid="dashboard-todos">
						{#each openTodos as todo (todo.id)}
							<TodoRow {todo} onToggle={toggleTodo} busy={todoBusy === todo.id} />
						{/each}
					</ul>
				</div>
			</section>
		{/if}

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

		<!-- Recent activity -->
		<section
			class="card border border-base-300/60 bg-base-100 shadow-sm"
			aria-labelledby="activity"
		>
			<div class="card-body gap-1 p-5">
				<div class="flex items-baseline justify-between">
					<h2 id="activity" class="font-semibold">Recent activity</h2>
					<a href="/activity" class="link text-sm link-primary no-underline">See all</a>
				</div>
				{#if data.activity.length === 0}
					<p class="py-4 text-sm text-base-content/60">Changes either of you make show up here.</p>
				{:else}
					<ul class="divide-y divide-base-300/60" data-testid="recent-activity">
						{#each data.activity as ev (ev.id)}
							<ActivityRow event={ev} mine={ev.actorId === data.user?.id} />
						{/each}
					</ul>
				{/if}
			</div>
		</section>
	{/if}
</main>

{#if s}
	<!-- Quick add -->
	<div class="fixed right-4 bottom-[calc(5rem+env(safe-area-inset-bottom))] z-30 print:hidden">
		<button
			type="button"
			class="btn btn-circle shadow-lg btn-lg btn-primary"
			aria-label="Quick add"
			data-testid="quick-add"
			onclick={openSheet}
		>
			<Icon name="plus" size={26} strokeWidth={2.5} />
		</button>
	</div>

	<BottomSheet bind:open={sheet} title={quickMode === 'expense' ? 'Add an expense' : 'Quick add'}>
		{#if quickMode === 'menu'}
			<ul class="flex flex-col gap-2">
				<li>
					<button
						type="button"
						class="btn min-h-14 w-full justify-start gap-3 rounded-2xl border border-base-300 btn-ghost"
						onclick={() => (quickMode = 'expense')}
					>
						<span
							class="flex h-9 w-9 items-center justify-center rounded-xl bg-primary/10 text-primary"
						>
							<Icon name="shopping-bag" size={18} />
						</span>
						<span class="text-left">
							<span class="block font-medium">Expense</span>
							<span class="block text-xs font-normal text-base-content/60">A one-off purchase</span>
						</span>
					</button>
				</li>
				<li>
					<a
						href="/bills/new"
						class="btn min-h-14 w-full justify-start gap-3 rounded-2xl border border-base-300 btn-ghost"
					>
						<span
							class="flex h-9 w-9 items-center justify-center rounded-xl bg-secondary/10 text-secondary"
						>
							<Icon name="receipt" size={18} />
						</span>
						<span class="text-left">
							<span class="block font-medium">Bill</span>
							<span class="block text-xs font-normal text-base-content/60"
								>Recurring bill, subscription or income</span
							>
						</span>
					</a>
				</li>
				<li>
					<a
						href="/todos?add=1"
						class="btn min-h-14 w-full justify-start gap-3 rounded-2xl border border-base-300 btn-ghost"
					>
						<span
							class="flex h-9 w-9 items-center justify-center rounded-xl bg-accent/20 text-accent-content"
						>
							<Icon name="list-checks" size={18} />
						</span>
						<span class="text-left">
							<span class="block font-medium">To-pay</span>
							<span class="block text-xs font-normal text-base-content/60"
								>Something to remember, not a bill</span
							>
						</span>
					</a>
				</li>
			</ul>
		{:else}
			<form
				method="POST"
				action="?/quickExpense"
				class="flex flex-col gap-3"
				use:enhance={() => {
					quickSubmitting = true;
					return async ({ update }) => {
						await update();
						quickSubmitting = false;
					};
				}}
			>
				<input type="hidden" name="spentOn" value={todayIso} />
				{#if data.household?.defaultAccountId}
					<input type="hidden" name="accountId" value={data.household.defaultAccountId} />
					<input type="hidden" name="deduct" value="on" />
				{/if}
				{#if form?.error}
					<div class="alert text-sm alert-error" role="alert">{form.error}</div>
				{/if}
				<label class="form-control">
					<span class="label-text mb-1 text-sm font-medium">What was it?</span>
					<input
						name="name"
						type="text"
						required
						maxlength="80"
						class="input-bordered input min-h-12 w-full"
						placeholder="Coffee"
						defaultValue={form?.values?.name ?? ''}
					/>
				</label>
				<div class="grid grid-cols-2 gap-3">
					<label class="form-control">
						<span class="label-text mb-1 text-sm font-medium">Amount</span>
						<div class="input-bordered input flex min-h-12 w-full items-center gap-2">
							<span class="text-base-content/50">{currencySymbol()}</span>
							<input
								name="amount"
								type="text"
								inputmode="decimal"
								required
								class="grow tabular"
								placeholder="0.00"
								defaultValue={form?.values?.amount ?? ''}
							/>
						</div>
					</label>
					<label class="form-control">
						<span class="label-text mb-1 text-sm font-medium">Category</span>
						<select
							name="categoryId"
							class="select-bordered select min-h-12 w-full"
							defaultValue={form?.values?.categoryId ?? ''}
						>
							<option value="">None</option>
							{#each data.categories as c (c.id)}
								<option value={c.id}>{c.name}</option>
							{/each}
						</select>
					</label>
				</div>
				<div class="flex gap-2">
					<button class="btn min-h-12 flex-1 rounded-full btn-primary" disabled={quickSubmitting}>
						{#if quickSubmitting}<span class="loading loading-sm loading-spinner"></span>{/if}
						Add expense
					</button>
					<a href="/expenses/new" class="btn min-h-12 rounded-full btn-ghost">More options</a>
				</div>
			</form>
		{/if}
	</BottomSheet>
{/if}
