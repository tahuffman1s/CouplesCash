<script lang="ts">
	import { onDestroy, untrack } from 'svelte';
	import { SvelteURLSearchParams } from 'svelte/reactivity';
	import { goto, invalidateAll } from '$app/navigation';
	import { page } from '$app/state';
	import type { BulkResult, InboxCandidates, InboxItem, TransactionFeed } from '#lib/types.ts';
	import type { PageProps } from './$types';
	import AccountTxRow from '#lib/components/AccountTxRow.svelte';
	import AppBar from '#lib/components/AppBar.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import NoHousehold from '#lib/components/NoHousehold.svelte';
	import QuickAddSheet, { quickAddForm } from '#lib/components/QuickAddSheet.svelte';
	import ReviewRow from '#lib/components/ReviewRow.svelte';
	import TransactionSheet, { type SheetSection } from '#lib/components/TransactionSheet.svelte';
	import { apiFetch, ClientApiError } from '#lib/utils/client.ts';
	import { dayHeading } from '#lib/utils/dates.ts';
	import { signedMoney } from '#lib/utils/money.ts';
	import {
		bulkEligible,
		groupByDay,
		matchesQuery,
		reviewAction,
		transactionFeedQuery
	} from '#lib/utils/transactions.ts';
	import { review as reviewBadge } from '#lib/state/review.svelte.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data, form }: PageProps = $props();

	// --- one map of rows by id: server data plus every local change (optimistic / confirmed)
	let local = $state<Record<string, InboxItem>>({});
	let extra = $state<InboxItem[]>([]); // "Load more" pages
	let nextBefore = $derived<string | null>(data.feed?.nextBefore ?? null);
	$effect(() => {
		void data.feed;
		void data.review;
		local = {};
		extra = [];
	});
	const rows = $derived.by(() => {
		const seen: Record<string, InboxItem> = {};
		const add = (r: InboxItem) => {
			if (!(r.id in seen)) seen[r.id] = local[r.id] ?? r;
		};
		for (const r of data.review) add(r);
		for (const r of data.feed?.pending ?? []) add(r);
		for (const r of data.feed?.items ?? []) add(r);
		for (const r of extra) add(r);
		return Object.values(seen);
	});
	// v7: fun-money accounts never get review buttons and stay out of the list unless shown
	const funIds = $derived(new Set(data.accounts.filter((a) => a.role === 'fun').map((a) => a.id)));
	const isFun = (r: InboxItem) => !!r.accountId && funIds.has(r.accountId);
	const showFunRows = $derived(data.showFun || (!!data.accountId && funIds.has(data.accountId)));
	const visible = $derived(rows.filter((r) => showFunRows || !isFun(r)));
	const reviewRows = $derived(
		rows.filter(
			(r) =>
				r.status === 'pending' &&
				!isFun(r) &&
				(!data.accountId || r.accountId === data.accountId) &&
				(!data.q || matchesQuery(r, data.q))
		)
	);
	// v7: Pending lists EVERY in-flight row of the shown accounts (to review or not, transfer legs
	// included), so its count is the bank's pending count; a row to review shows in both sections
	const pendingRows = $derived(
		visible.filter(
			(r) =>
				r.pending &&
				(!data.accountId || r.accountId === data.accountId) &&
				(!data.q || matchesQuery(r, data.q))
		)
	);
	const postedRows = $derived(
		visible
			.filter((r) => !r.pending && (r.status !== 'pending' || isFun(r)))
			.toSorted((a, b) => b.postedOn.localeCompare(a.postedOn))
	);
	const days = $derived(groupByDay(postedRows));
	const pendingOut = $derived(
		pendingRows.filter((r) => r.amountCents < 0).reduce((s, r) => s - r.amountCents, 0)
	);
	const suggested = $derived(reviewRows.filter(bulkEligible).length);
	const filtered = $derived(!!data.accountId || !!data.q);

	let busy = $state<string[]>([]);
	const isBusy = (id: string) => busy.includes(id);
	function put(next: InboxItem) {
		local = { ...local, [next.id]: next };
	}
	const post = (path: string, body?: unknown) =>
		apiFetch<InboxItem>(path, {
			method: 'POST',
			body: body === undefined ? undefined : JSON.stringify(body)
		});

	/** Optimistic: the row leaves To review at once; Undo (unmatch) puts it back. */
	async function act(
		item: InboxItem,
		optimistic: Partial<InboxItem>,
		request: () => Promise<InboxItem>,
		label: (next: InboxItem) => string
	) {
		if (isBusy(item.id)) return;
		busy = [...busy, item.id];
		put({ ...item, ...optimistic });
		reviewBadge.add(-1);
		try {
			const next = await request();
			put(next);
			toasts.show(label(next), 'success', 6000, { label: 'Undo', run: () => void undo(next) });
		} catch (e) {
			put(item);
			reviewBadge.add(1);
			if (e instanceof ClientApiError && (e.status === 400 || e.status === 409)) {
				toasts.show(e.problem.detail || "That doesn't fit this transaction.", 'error');
				openSheet(item);
			} else toasts.show('Could not update. Check your connection.', 'error');
		} finally {
			busy = busy.filter((k) => k !== item.id);
		}
	}
	async function undo(item: InboxItem) {
		put({
			...item,
			status: 'pending',
			confirmed: null,
			ignoreReason: null,
			transfer: null,
			match: null,
			expense: null
		});
		reviewBadge.add(1);
		try {
			put(await post(`/inbox/${item.id}/unmatch`));
			toasts.show('Back in To review', 'info');
		} catch {
			put(item);
			reviewBadge.add(-1);
			toasts.show('Could not undo. Check your connection.', 'error');
		}
	}

	async function onPrimary(item: InboxItem) {
		const a = reviewAction(item);
		if (a.kind === 'choose') return openSheet(item);
		if (a.kind === 'occurrence')
			return act(
				item,
				{ status: 'confirmed' },
				() =>
					post(`/inbox/${item.id}/confirm`, {
						as: 'occurrence',
						itemId: a.itemId,
						dueDate: a.dueDate
					}),
				(n) => `${n.match?.itemName ?? a.label.replace(/^It's /, '')} marked paid`
			);
		if (a.kind === 'expense')
			return act(
				item,
				{ status: 'confirmed' },
				() => post(`/inbox/${item.id}/confirm`, { as: 'expense', categoryId: a.categoryId }),
				() => `Expense added in ${a.label}`
			);
		// transfer: pair with the one obvious other side, else one-sided; several → let them pick
		let peers: InboxItem[] = [];
		try {
			peers = (await apiFetch<InboxCandidates>(`/inbox/${item.id}/candidates?days=30`))
				.transferPeers;
		} catch {
			// one-sided is still a fine answer
		}
		if (peers.length > 1) return openSheet(item, 'transfer');
		const peer = peers[0] ?? null;
		await act(
			item,
			{ status: 'ignored', ignoreReason: 'transfer' },
			() => post(`/inbox/${item.id}/transfer`, { peerId: peer?.id ?? null }),
			() => (peer?.accountName ? `Transfer with ${peer.accountName}` : 'Marked as a transfer')
		);
		// the other side changed too
		if (peer) void invalidateAll();
	}
	function onIgnore(item: InboxItem) {
		void act(
			item,
			{ status: 'ignored', ignoreReason: 'manual' },
			() => post(`/inbox/${item.id}/ignore`),
			() => `Ignored ${item.payee || item.description}`
		);
	}

	// --- "Do all suggestions"
	let bulkBusy = $state(false);
	async function doAll() {
		if (bulkBusy) return;
		bulkBusy = true;
		try {
			const r = await apiFetch<BulkResult>('/inbox/bulk', {
				method: 'POST',
				body: JSON.stringify({ action: 'confirm-suggested' })
			});
			const n = r.confirmed + r.ignored;
			toasts.show(`${n} done${r.skipped ? ` · ${r.skipped} still need you` : ''}`, 'success');
			await invalidateAll();
		} catch {
			toasts.show('Could not update. Check your connection.', 'error');
		} finally {
			bulkBusy = false;
		}
	}

	// --- the shared transaction sheet
	let sheet = $state(false);
	let selectedId = $state<string | null>(null);
	let initial = $state<SheetSection | undefined>(undefined);
	const selected = $derived(rows.find((r) => r.id === selectedId) ?? null);
	function openSheet(item: InboxItem, section?: SheetSection) {
		initial = section;
		selectedId = item.id;
		sheet = true;
	}
	function onSheetChange(next: InboxItem) {
		const prev = rows.find((r) => r.id === next.id);
		if (prev)
			reviewBadge.add((next.status === 'pending' ? 1 : 0) - (prev.status === 'pending' ? 1 : 0));
		put(next);
	}

	// --- filters: account chips are links; search updates the URL after a pause
	let q = $state(untrack(() => data.q ?? ''));
	let debounce: ReturnType<typeof setTimeout> | undefined;
	onDestroy(() => clearTimeout(debounce));
	const hrefFor = (accountId: string | null, fun = data.showFun) => {
		const s = new SvelteURLSearchParams();
		if (accountId) s.set('account', accountId);
		if (data.q) s.set('q', data.q);
		if (fun) s.set('fun', '1');
		const qs = s.toString();
		return qs ? `?${qs}` : '/transactions';
	};
	function onSearch() {
		clearTimeout(debounce);
		debounce = setTimeout(() => {
			const s = new SvelteURLSearchParams(page.url.searchParams.toString());
			if (q.trim()) s.set('q', q.trim());
			else s.delete('q');
			const qs = s.toString();
			void goto(qs ? `?${qs}` : '/transactions', { reset: false, replace: true });
		}, 300);
	}

	let loading = $state(false);
	async function loadMore() {
		if (!nextBefore || loading) return;
		loading = true;
		try {
			const res = await apiFetch<TransactionFeed>(
				`/transactions${transactionFeedQuery({ accountId: data.accountId, q: data.q, before: nextBefore, limit: 50 })}`
			);
			extra = [...extra, ...res.items];
			nextBefore = res.nextBefore;
		} catch {
			toasts.show('Could not load more.', 'error');
		} finally {
			loading = false;
		}
	}

	$effect(() => {
		if (data.added) untrack(() => toasts.show('Expense added', 'success'));
	});

	let quickOpen = $state(false);
	const quick = $derived(quickAddForm(form));
	const linkedAccounts = $derived(
		data.accounts.filter((a) => data.showFun || a.role !== 'fun' || a.id === data.accountId)
	);
	const hasFun = $derived(funIds.size > 0);
</script>

<svelte:head><title>Transactions · CouplesCash</title></svelte:head>

<AppBar title="Transactions">
	{#snippet action()}
		{#if data.feed || data.household}
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
	{#if !data.household}
		<NoHousehold />
	{:else}
		<!-- filters -->
		<div class="flex flex-col gap-2">
			{#if linkedAccounts.length > 1}
				<div class="flex gap-2 overflow-x-auto pb-1" role="radiogroup" aria-label="Account">
					<a
						href={hrefFor(null)}
						role="radio"
						aria-checked={!data.accountId}
						class="btn min-h-10 shrink-0 rounded-full px-4 btn-sm {!data.accountId
							? 'btn-neutral'
							: 'border border-base-300 btn-ghost'}"
						data-sveltekit-noscroll
						data-sveltekit-replacestate>All accounts</a
					>
					{#each linkedAccounts as a (a.id)}
						<a
							href={hrefFor(a.id)}
							role="radio"
							aria-checked={data.accountId === a.id}
							class="btn min-h-10 shrink-0 rounded-full px-4 btn-sm {data.accountId === a.id
								? 'btn-neutral'
								: 'border border-base-300 btn-ghost'}"
							data-sveltekit-noscroll
							data-sveltekit-replacestate>{a.name}</a
						>
					{/each}
				</div>
			{/if}
			{#if hasFun}
				<a
					href={hrefFor(
						data.accountId && funIds.has(data.accountId) ? null : data.accountId,
						!data.showFun
					)}
					class="flex min-h-11 items-center gap-2 self-start px-1 text-xs text-base-content/65"
					data-sveltekit-noscroll
					data-sveltekit-replacestate
					data-testid="fun-toggle"
				>
					<Icon name={data.showFun ? 'eye-off' : 'eye'} size={14} />
					{data.showFun ? 'Hide fun money' : 'Show fun money'}
				</a>
			{/if}
			<label class="input-bordered input flex min-h-11 w-full items-center gap-2">
				<Icon name="search" size={16} class="text-base-content/50" />
				<input
					type="search"
					class="grow"
					placeholder="Search"
					aria-label="Search transactions"
					bind:value={q}
					oninput={onSearch}
				/>
			</label>
		</div>

		<!-- To review -->
		<section aria-labelledby="review-h" data-testid="to-review">
			<div class="mb-2 flex items-center justify-between gap-2 px-1">
				<h2
					id="review-h"
					class="text-sm font-semibold tracking-wide text-base-content/70 uppercase"
				>
					To review
					{#if reviewRows.length}<span class="ml-1 badge badge-sm tabular badge-primary"
							>{reviewRows.length}</span
						>{/if}
				</h2>
				{#if suggested > 0}
					<button
						type="button"
						class="btn min-h-11 rounded-full px-4 btn-secondary btn-sm"
						disabled={bulkBusy}
						data-testid="do-all"
						onclick={doAll}
					>
						{#if bulkBusy}<span class="loading loading-xs loading-spinner"></span>{:else}<Icon
								name="check-check"
								size={16}
							/>{/if}
						Do all suggestions ({suggested})
					</button>
				{/if}
			</div>
			{#if reviewRows.length === 0}
				<p
					class="flex items-center gap-2 rounded-2xl bg-success/10 px-4 py-3 text-sm font-medium"
					data-testid="all-caught-up"
				>
					<Icon name="circle-check" size={18} class="text-success" /> All caught up
				</p>
			{:else}
				<ul class="flex flex-col gap-2">
					{#each reviewRows as item (item.id)}
						<ReviewRow
							{item}
							busy={isBusy(item.id)}
							onprimary={onPrimary}
							onelse={(i) => openSheet(i)}
							onignore={onIgnore}
						/>
					{/each}
				</ul>
			{/if}
		</section>

		<!-- Pending (pinned) -->
		{#if pendingRows.length}
			<section aria-labelledby="pending-h" data-testid="pending-section">
				<div class="mb-1 flex items-baseline justify-between px-1">
					<h2
						id="pending-h"
						class="text-sm font-semibold tracking-wide text-base-content/70 uppercase"
					>
						Pending
						<span class="ml-1 badge badge-sm tabular" data-testid="pending-count"
							>{pendingRows.length}</span
						>
					</h2>
					<span class="text-xs text-base-content/60 tabular" data-testid="pending-total"
						>{signedMoney(-pendingOut)}</span
					>
				</div>
				<ul
					class="card divide-y divide-base-300/60 border border-warning/40 bg-base-100 px-4 shadow-sm"
				>
					{#each pendingRows as tx (tx.id)}
						<AccountTxRow {tx} onopen={(t) => openSheet(t)} showAccount />
					{/each}
				</ul>
			</section>
		{/if}

		<!-- Posted, by day -->
		{#if !data.feed}
			<p class="py-6 text-center text-sm text-base-content/60">Could not load transactions.</p>
		{:else if postedRows.length === 0 && reviewRows.length === 0 && pendingRows.length === 0}
			{#if filtered}
				<p class="py-6 text-center text-sm text-base-content/60" data-testid="tx-empty">
					Nothing matches.
				</p>
			{:else}
				<EmptyState
					icon="list"
					title="No transactions yet"
					body="Link your banks and every transaction lands here. Anything that needs one of you shows up under To review."
				>
					<div class="mt-3 flex flex-wrap justify-center gap-2">
						<a href="/settings/banks" class="btn min-h-11 rounded-full px-5 btn-primary"
							><Icon name="landmark" size={16} /> Link a bank</a
						>
					</div>
				</EmptyState>
			{/if}
		{:else}
			{#each days as g (g.day)}
				<section aria-labelledby="tx-{g.day}" data-testid="tx-day" data-date={g.day}>
					<div class="mb-1 flex items-baseline justify-between px-1">
						<h2
							id="tx-{g.day}"
							class="text-sm font-semibold tracking-wide text-base-content/70 uppercase"
						>
							{dayHeading(g.day)}
						</h2>
						<span class="text-xs text-base-content/60 tabular" data-testid="tx-day-net"
							>{signedMoney(g.netCents)}</span
						>
					</div>
					<ul
						class="card divide-y divide-base-300/60 border border-base-300/60 bg-base-100 px-4 shadow-sm"
					>
						{#each g.items as tx (tx.id)}
							<AccountTxRow {tx} onopen={(t) => openSheet(t)} showAccount={!data.accountId} />
						{/each}
					</ul>
				</section>
			{/each}
			{#if nextBefore}
				<button
					type="button"
					class="btn min-h-12 rounded-full border border-base-300 btn-ghost"
					disabled={loading}
					onclick={loadMore}
				>
					{#if loading}<span class="loading loading-sm loading-spinner"></span>{/if}
					Load more
				</button>
			{/if}
		{/if}
	{/if}
</main>

{#if data.household}
	<QuickAddSheet
		bind:open={quickOpen}
		categories={data.categories}
		accounts={data.accounts}
		defaultAccountId={data.accounts.find((a) => a.role === 'bills')?.id ??
			data.household.defaultAccountId}
		form={quick}
	/>
{/if}

<TransactionSheet
	bind:open={sheet}
	item={selected}
	{initial}
	categories={data.categories}
	onchange={onSheetChange}
/>
