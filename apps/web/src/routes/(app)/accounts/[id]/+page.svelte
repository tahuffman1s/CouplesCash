<script lang="ts">
	import { enhance } from '$app/forms';
	import { invalidateAll } from '$app/navigation';
	import type {
		Account,
		AccountTransaction,
		AccountTransactionsPage,
		BalanceSnapshot,
		InboxItem,
		TxFilter
	} from '#lib/types.ts';
	import { LIABILITY_TYPES, TX_FILTERS } from '#lib/types.ts';
	import AccountTxRow from '#lib/components/AccountTxRow.svelte';
	import TransactionSheet from '#lib/components/TransactionSheet.svelte';
	import { accountTxQuery, groupByDay } from '#lib/utils/transactions.ts';
	import { signedAmount } from '#lib/utils/labels.ts';
	import { addMonths, dayHeading, formatMonth, today } from '#lib/utils/dates.ts';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import AccountForm from '#lib/components/AccountForm.svelte';
	import Sparkline from '#lib/charts/Sparkline.svelte';
	import { ACCOUNT_TYPE_LABEL } from '#lib/utils/labels.ts';
	import { centsToInput, formatMoney, parseMoney } from '#lib/utils/money.ts';
	import { formatTimestamp } from '#lib/utils/dates.ts';
	import { apiFetch } from '#lib/utils/client.ts';
	import { onDestroy, untrack } from 'svelte';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data, form }: PageProps = $props();

	let account = $derived<Account>(data.account);
	let history = $derived<BalanceSnapshot[]>(data.history);

	let editing = $state(false);
	let submitting = $state(false);
	let quick = $state('');
	let quickBusy = $state(false);
	let confirmDelete = $state(false);

	const liability = $derived(LIABILITY_TYPES.has(account.type));
	const spark = $derived(
		[...history].reverse().map((h) => ({ at: h.recordedAt, value: h.balanceCents }))
	);

	async function quickUpdate(e: SubmitEvent) {
		e.preventDefault();
		const cents = parseMoney(quick);
		if (cents === null) {
			toasts.show('Enter a number like 1250.00', 'error');
			return;
		}
		const previous = account;
		account = { ...account, balanceCents: cents };
		quickBusy = true;
		try {
			account = await apiFetch<Account>(`/accounts/${account.id}/balance`, {
				method: 'POST',
				body: JSON.stringify({ balanceCents: cents })
			});
			history = [
				{
					balanceCents: cents,
					recordedAt: new Date().toISOString(),
					recordedBy: data.user?.id ?? ''
				},
				...history
			];
			quick = '';
			toasts.show('Balance updated', 'success');
			void invalidateAll();
		} catch {
			account = previous;
			toasts.show('Could not update the balance.', 'error');
		} finally {
			quickBusy = false;
		}
	}

	$effect(() => {
		if (form?.saved) {
			editing = false;
			toasts.show('Account saved', 'success');
		}
	});
	$effect(() => {
		if (data.created) toasts.show('Account added', 'success');
	});

	// --- v5: Transactions tab
	const EMPTY: AccountTransactionsPage = {
		items: [],
		nextBefore: null,
		totals: { inCents: 0, outCents: 0, pendingOutCents: 0, count: 0 }
	};
	let txPage = $derived<AccountTransactionsPage>(data.transactions ?? EMPTY);
	let txItems = $derived<AccountTransaction[]>(data.transactions?.items ?? []);
	let filter = $state<TxFilter>(untrack(() => data.filter));
	let q = $state('');
	let txLoading = $state(false);
	let moreLoading = $state(false);
	let seq = 0;
	let debounce: ReturnType<typeof setTimeout> | undefined;
	onDestroy(() => clearTimeout(debounce));

	const groups = $derived(groupByDay(txItems));
	const available = $derived(account.availableCents ?? null);
	const pendingOut = $derived(account.pendingOutCents ?? 0);
	const availableLine = $derived(
		[
			available !== null ? `Available ${formatMoney(available)}` : null,
			pendingOut > 0 ? `${formatMoney(pendingOut)} pending` : null
		]
			.filter(Boolean)
			.join(' · ')
	);
	const isCurrentMonth = $derived(data.month === today().slice(0, 7));

	async function fetchTx(opts: { append?: boolean } = {}) {
		const mine = ++seq;
		if (opts.append) moreLoading = true;
		else txLoading = true;
		try {
			const page = await apiFetch<AccountTransactionsPage>(
				`/accounts/${account.id}/transactions${accountTxQuery({
					filter,
					q: q.trim() || null,
					month: data.month,
					before: opts.append ? txPage.nextBefore : null,
					limit: 50
				})}`
			);
			if (mine !== seq) return;
			if (opts.append) {
				const seen = new Set(txItems.map((t) => t.id));
				txItems = [...txItems, ...page.items.filter((t) => !seen.has(t.id))];
				txPage = { ...page, items: txItems };
			} else {
				txItems = page.items;
				txPage = page;
			}
		} catch {
			if (mine === seq) toasts.show('Could not load transactions.', 'error');
		} finally {
			if (mine === seq) {
				txLoading = false;
				moreLoading = false;
			}
		}
	}
	// a month change reloads page 1 server-side (filter all); re-apply the current filter/search
	$effect(() => {
		void data.transactions;
		untrack(() => {
			if (filter !== data.filter || q.trim()) void fetchTx();
		});
	});
	function setFilter(next: TxFilter) {
		if (filter === next) return;
		filter = next;
		void fetchTx();
	}
	function onSearch() {
		clearTimeout(debounce);
		debounce = setTimeout(() => void fetchTx(), 300);
	}

	let sheet = $state(false);
	let selectedId = $state<string | null>(null);
	const selected = $derived(txItems.find((t) => t.id === selectedId) ?? null);
	function openTx(tx: AccountTransaction | InboxItem) {
		selectedId = tx.id;
		sheet = true;
	}
	function onTxChange(next: InboxItem) {
		txItems = txItems.map((t) => (t.id === next.id ? { ...t, ...next } : t));
	}
	const monthHref = (n: number) =>
		`?tab=transactions&month=${addMonths(`${data.month}-01`, n).slice(0, 7)}${filter !== 'all' ? `&filter=${filter}` : ''}`;
</script>

<svelte:head><title>{account.name} · CouplesCash</title></svelte:head>

<AppBar
	title={account.name}
	subtitle="{account.institution} · {ACCOUNT_TYPE_LABEL[account.type]}"
	back="/week"
>
	{#snippet action()}
		<button
			type="button"
			class="btn btn-circle min-h-11 min-w-11 btn-ghost btn-sm"
			aria-label={editing ? 'Stop editing' : 'Edit account'}
			aria-pressed={editing}
			onclick={() => (editing = !editing)}
		>
			<Icon name={editing ? 'x' : 'pencil'} size={20} />
		</button>
	{/snippet}
</AppBar>

<main class="flex flex-col gap-4 px-4 pt-2">
	{#if !editing}
		<div class="join w-full" role="tablist" aria-label="Account view">
			<a
				href="/accounts/{account.id}"
				role="tab"
				aria-selected={data.tab === 'overview'}
				class="btn join-item min-h-11 flex-1 {data.tab === 'overview'
					? 'btn-neutral'
					: 'border border-base-300 btn-ghost'}"
				data-sveltekit-noscroll>Overview</a
			>
			<a
				href="/accounts/{account.id}?tab=transactions"
				role="tab"
				aria-selected={data.tab === 'transactions'}
				class="btn join-item min-h-11 flex-1 {data.tab === 'transactions'
					? 'btn-neutral'
					: 'border border-base-300 btn-ghost'}"
				data-sveltekit-noscroll>Transactions</a
			>
		</div>
	{/if}
	{#if !editing && data.tab === 'transactions'}
		<section class="card border border-base-300/60 bg-base-100 shadow-sm" aria-label="Summary">
			<div class="card-body gap-3 p-5">
				<div>
					<p class="text-xs text-base-content/60">{liability ? 'Amount owed' : 'Balance'}</p>
					<p
						class="text-3xl font-bold tracking-tight tabular {liability ? 'text-error' : ''}"
						data-testid="tx-balance"
					>
						{formatMoney(account.balanceCents)}
					</p>
					{#if available !== null || pendingOut > 0}
						<p class="text-sm text-base-content/70" data-testid="tx-available">
							{availableLine}
						</p>
					{/if}
				</div>
				<div class="flex items-center justify-between">
					<a
						href={monthHref(-1)}
						class="btn btn-circle min-h-11 min-w-11 btn-ghost"
						aria-label="Previous month"
						data-sveltekit-noscroll
					>
						<Icon name="chevron-left" size={20} />
					</a>
					<h2 class="text-sm font-semibold" data-testid="tx-month">{formatMonth(data.month)}</h2>
					<a
						href={monthHref(1)}
						class="btn btn-circle min-h-11 min-w-11 btn-ghost {isCurrentMonth
							? 'btn-disabled'
							: ''}"
						aria-label="Next month"
						aria-disabled={isCurrentMonth}
						data-sveltekit-noscroll
					>
						<Icon name="chevron-right" size={20} />
					</a>
				</div>
				<div class="grid grid-cols-2 gap-2 text-sm">
					<div class="rounded-xl bg-base-200/70 px-3 py-2">
						<p class="text-xs text-base-content/60">Money in</p>
						<p class="font-semibold text-success tabular" data-testid="tx-in">
							{formatMoney(txPage.totals.inCents)}
						</p>
					</div>
					<div class="rounded-xl bg-base-200/70 px-3 py-2">
						<p class="text-xs text-base-content/60">Money out</p>
						<p class="font-semibold tabular" data-testid="tx-out">
							{formatMoney(Math.abs(txPage.totals.outCents))}
						</p>
					</div>
				</div>
			</div>
		</section>

		<div class="flex flex-col gap-2">
			<div class="flex gap-2 overflow-x-auto pb-1" role="radiogroup" aria-label="Filter">
				{#each account.role === 'fun' ? TX_FILTERS.filter((x) => x.value !== 'review' && x.value !== 'matched') : TX_FILTERS as f (f.value)}
					<button
						type="button"
						role="radio"
						aria-checked={filter === f.value}
						class="btn min-h-10 shrink-0 rounded-full px-4 btn-sm {filter === f.value
							? 'btn-neutral'
							: 'border border-base-300 btn-ghost'}"
						onclick={() => setFilter(f.value)}
					>
						{f.label}
					</button>
				{/each}
			</div>
			<label class="input-bordered input flex min-h-11 w-full items-center gap-2">
				<Icon name="search" size={16} class="text-base-content/50" />
				<input
					type="search"
					class="grow"
					placeholder="Search description, payee or memo"
					aria-label="Search transactions"
					bind:value={q}
					oninput={onSearch}
				/>
			</label>
		</div>

		<div class="flex flex-col gap-3 {txLoading ? 'opacity-60' : ''}" aria-busy={txLoading}>
			{#if txItems.length === 0}
				<p class="py-8 text-center text-sm text-base-content/60" data-testid="tx-empty">
					{q.trim() || filter !== 'all'
						? 'Nothing matches.'
						: `No transactions in ${formatMonth(data.month)}.`}
				</p>
			{:else}
				{#each groups as g (g.day)}
					<section aria-labelledby="tx-{g.day}" data-testid="tx-day" data-date={g.day}>
						<div class="mb-1 flex items-baseline justify-between px-1">
							<h3
								id="tx-{g.day}"
								class="text-sm font-semibold tracking-wide text-base-content/70 uppercase"
							>
								{dayHeading(g.day)}
							</h3>
							<span class="text-xs text-base-content/60 tabular" data-testid="tx-day-net"
								>{signedAmount(g.netCents, formatMoney)}</span
							>
						</div>
						<ul
							class="card divide-y divide-base-300/60 border border-base-300/60 bg-base-100 px-4 shadow-sm"
						>
							{#each g.items as tx (tx.id)}
								<AccountTxRow {tx} onopen={openTx} />
							{/each}
						</ul>
					</section>
				{/each}
				{#if txPage.nextBefore}
					<button
						type="button"
						class="btn min-h-12 rounded-full border border-base-300 btn-ghost"
						disabled={moreLoading}
						onclick={() => fetchTx({ append: true })}
					>
						{#if moreLoading}<span class="loading loading-sm loading-spinner"></span>{/if}
						Load more
					</button>
				{/if}
			{/if}
		</div>
	{:else if !editing}
		<section class="card border border-base-300/60 bg-base-100 shadow-sm">
			<div class="card-body gap-4 p-5">
				<div class="flex items-center gap-3">
					<span
						class="flex h-12 w-12 items-center justify-center rounded-2xl text-white"
						style:background={account.color}
					>
						<Icon name={account.icon} size={24} />
					</span>
					<div>
						<p class="text-xs text-base-content/60">{liability ? 'Amount owed' : 'Balance'}</p>
						<p
							class="text-3xl font-bold tracking-tight tabular {liability ? 'text-error' : ''}"
							data-testid="balance"
						>
							{formatMoney(account.balanceCents)}
						</p>
					</div>
				</div>

				<form class="flex gap-2" onsubmit={quickUpdate} aria-label="Quick balance update">
					<label class="input-bordered input flex min-h-12 flex-1 items-center gap-2">
						<span class="text-base-content/50">$</span>
						<input
							type="text"
							inputmode="decimal"
							class="grow tabular"
							placeholder={centsToInput(account.balanceCents)}
							aria-label="New balance"
							bind:value={quick}
						/>
					</label>
					<button class="btn min-h-12 rounded-xl btn-secondary" disabled={quickBusy || !quick}>
						{#if quickBusy}<span class="loading loading-sm loading-spinner"></span>{/if}
						Update
					</button>
				</form>

				{#if account.note}<p class="text-sm text-base-content/70">{account.note}</p>{/if}
			</div>
		</section>

		<section class="card border border-base-300/60 bg-base-100 shadow-sm" aria-labelledby="history">
			<div class="card-body gap-3 p-5">
				<h2 id="history" class="font-semibold">Balance history</h2>
				<Sparkline points={spark} color={account.color} />
				{#if history.length}
					<ul class="divide-y divide-base-300/60 text-sm">
						{#each history.slice(0, 8) as h, i (h.recordedAt + i)}
							<li class="flex justify-between py-2">
								<span class="text-base-content/60">{formatTimestamp(h.recordedAt)}</span>
								<span class="font-medium tabular">{formatMoney(h.balanceCents)}</span>
							</li>
						{/each}
					</ul>
				{/if}
			</div>
		</section>
	{:else}
		<form
			method="POST"
			action="?/update"
			class="card border border-base-300/60 bg-base-100 shadow-sm"
			use:enhance={() => {
				submitting = true;
				return async ({ update }) => {
					await update({ reset: false });
					submitting = false;
				};
			}}
		>
			<div class="card-body gap-4 p-5">
				<AccountForm {account} values={form?.values} error={form?.error} />
				<button class="btn min-h-12 rounded-full btn-primary" disabled={submitting}>
					{#if submitting}<span class="loading loading-sm loading-spinner"></span>{/if}
					Save changes
				</button>
			</div>
		</form>

		<form
			method="POST"
			action="?/delete"
			use:enhance
			class="card border border-error/30 bg-base-100"
		>
			<div class="card-body gap-2 p-5">
				<h2 class="font-semibold">Remove account</h2>
				<p class="text-sm text-base-content/65">
					Hides this account and its history from both of you. Bills linked to it stay.
				</p>
				{#if confirmDelete}
					<div class="flex gap-2">
						<button class="btn min-h-11 flex-1 rounded-full btn-error">Yes, remove</button>
						<button
							type="button"
							class="btn min-h-11 flex-1 rounded-full btn-ghost"
							onclick={() => (confirmDelete = false)}
						>
							Cancel
						</button>
					</div>
				{:else}
					<button
						type="button"
						class="btn min-h-11 rounded-full btn-outline btn-error"
						onclick={() => (confirmDelete = true)}
					>
						<Icon name="trash" size={18} /> Remove account
					</button>
				{/if}
			</div>
		</form>
	{/if}
</main>

<TransactionSheet
	bind:open={sheet}
	item={selected}
	categories={data.categories}
	onchange={onTxChange}
/>
