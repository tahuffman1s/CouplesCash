<script lang="ts">
	import { invalidateAll } from '$app/navigation';
	import type { BulkResult, InboxItem, InboxPage, InboxStatus } from '#lib/types.ts';
	import type { InboxConfirmBody } from '#lib/server/api.ts';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import NoHousehold from '#lib/components/NoHousehold.svelte';
	import TransactionSheet, { type SheetSection } from '#lib/components/TransactionSheet.svelte';
	import InboxRow from '#lib/components/InboxRow.svelte';
	import { apiFetch, ClientApiError } from '#lib/utils/client.ts';
	import { dayHeading } from '#lib/utils/dates.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data }: PageProps = $props();

	const CHIPS: { value: InboxStatus; label: string }[] = [
		{ value: 'pending', label: 'Pending' },
		{ value: 'confirmed', label: 'Confirmed' },
		{ value: 'ignored', label: 'Ignored' }
	];

	// Local, writable copies; reset whenever the server data changes (navigation / invalidateAll).
	let items = $derived<InboxItem[]>(data.page?.items ?? []);
	let nextBefore = $derived<string | null>(data.page?.nextBefore ?? null);
	let pending = $derived<number>(data.summary?.pending ?? 0);
	let busy = $state<string[]>([]);
	let loading = $state(false);
	let bulkBusy = $state(false);

	const suggestedCount = $derived(
		data.status === 'pending'
			? items.filter(
					(i) =>
						i.status === 'pending' &&
						i.suggestion &&
						i.suggestion.kind !== 'transfer' &&
						i.suggestion.confidence >= 70
				).length
			: (data.summary?.suggested ?? 0)
	);
	const groups = $derived.by(() => {
		const out: { day: string; items: InboxItem[] }[] = [];
		for (const it of items) {
			const last = out[out.length - 1];
			if (last && last.day === it.postedOn) last.items.push(it);
			else out.push({ day: it.postedOn, items: [it] });
		}
		return out;
	});

	const isBusy = (id: string) => busy.includes(id);
	function replace(next: InboxItem) {
		items = items.map((i) => (i.id === next.id ? next : i));
	}
	async function run(
		item: InboxItem,
		optimistic: Partial<InboxItem>,
		request: () => Promise<InboxItem>,
		success: (next: InboxItem) => string,
		pendingDelta: number
	) {
		if (isBusy(item.id)) return;
		const previous = item;
		replace({ ...item, ...optimistic });
		pending = Math.max(0, pending + pendingDelta);
		busy = [...busy, item.id];
		try {
			const next = await request();
			replace(next);
			toasts.show(success(next), 'success');
		} catch (e) {
			replace(previous);
			pending = Math.max(0, pending - pendingDelta);
			toasts.show(
				e instanceof ClientApiError && e.status === 400
					? e.problem.detail || "That doesn't fit this transaction."
					: 'Could not update. Check your connection.',
				'error'
			);
		} finally {
			busy = busy.filter((k) => k !== item.id);
		}
	}

	function confirmWith(item: InboxItem, body: InboxConfirmBody, label: string) {
		return run(
			item,
			{ status: 'confirmed' },
			() =>
				apiFetch<InboxItem>(`/inbox/${item.id}/confirm`, {
					method: 'POST',
					body: JSON.stringify(body)
				}),
			() => label,
			-1
		);
	}
	function onConfirm(item: InboxItem) {
		const s = item.suggestion;
		if (!s) return void onPick(item);
		// a transfer needs its other side (or "Other account"): the sheet lists the candidates
		if (s.kind === 'transfer') return void onPick(item, 'transfer');
		if (s.kind === 'category')
			return void confirmWith(
				item,
				{ as: 'expense', categoryId: s.categoryId },
				`Expense added in ${s.categoryName}`
			);
		return void confirmWith(
			item,
			{ as: 'occurrence', itemId: s.itemId, dueDate: s.dueDate },
			`${s.itemName} marked paid`
		);
	}
	function onIgnore(item: InboxItem) {
		void run(
			item,
			{ status: 'ignored', ignoreReason: 'manual' },
			() => apiFetch<InboxItem>(`/inbox/${item.id}/ignore`, { method: 'POST' }),
			() => 'Ignored',
			-1
		);
	}
	function onRestore(item: InboxItem) {
		void run(
			item,
			{ status: 'pending', confirmed: null, ignoreReason: null, transfer: null },
			() => apiFetch<InboxItem>(`/inbox/${item.id}/restore`, { method: 'POST' }),
			() => 'Back in the inbox',
			+1
		);
	}

	// --- v5: the shared transaction sheet (tapping a row or its chip)
	let sheet = $state(false);
	let selectedId = $state<string | null>(null);
	let initial = $state<SheetSection | undefined>(undefined);
	const selected = $derived(items.find((i) => i.id === selectedId) ?? null);
	function onPick(item: InboxItem, section?: SheetSection) {
		initial = section;
		selectedId = item.id;
		sheet = true;
	}
	function onSheetChange(next: InboxItem) {
		const prev = items.find((i) => i.id === next.id);
		if (prev) pending = Math.max(0, pending + pendingDelta(prev, next));
		replace(next);
	}
	const pendingDelta = (a: InboxItem, b: InboxItem) =>
		(b.status === 'pending' ? 1 : 0) - (a.status === 'pending' ? 1 : 0);

	// --- bulk
	async function bulk(action: 'confirm-suggested' | 'ignore') {
		if (bulkBusy) return;
		bulkBusy = true;
		try {
			const r = await apiFetch<BulkResult>('/inbox/bulk', {
				method: 'POST',
				body: JSON.stringify({ action })
			});
			toasts.show(
				action === 'confirm-suggested'
					? `${r.confirmed} confirmed${r.skipped ? `, ${r.skipped} skipped` : ''}`
					: `${r.ignored} ignored`,
				'success'
			);
			await invalidateAll();
		} catch {
			toasts.show('Could not update. Check your connection.', 'error');
		} finally {
			bulkBusy = false;
		}
	}

	async function loadMore() {
		if (!nextBefore || loading) return;
		loading = true;
		try {
			const page = await apiFetch<InboxPage>(
				`/inbox?status=${data.status}&before=${encodeURIComponent(nextBefore)}&limit=50${data.status === 'ignored' ? '&includeHidden=1' : ''}`
			);
			const seen = new Set(items.map((i) => i.id));
			items = [...items, ...page.items.filter((i) => !seen.has(i.id))];
			nextBefore = page.nextBefore;
		} catch {
			toasts.show('Could not load more.', 'error');
		} finally {
			loading = false;
		}
	}

	const subtitle = $derived(
		data.summary
			? `${pending} to review${data.summary.linkCount ? ` · ${data.summary.linkCount} bank${data.summary.linkCount === 1 ? '' : 's'} linked` : ''}`
			: undefined
	);
</script>

<svelte:head><title>Inbox · CouplesCash</title></svelte:head>

<AppBar title="Inbox" back="/more" {subtitle}>
	{#snippet action()}
		{#if data.page && data.status === 'pending' && items.some((i) => i.status === 'pending')}
			<div class="dropdown dropdown-end">
				<button
					type="button"
					class="btn btn-circle min-h-11 min-w-11 btn-ghost btn-sm"
					aria-label="More actions"
					tabindex="0"
				>
					<Icon name="ellipsis-vertical" size={20} />
				</button>
				<ul
					class="menu dropdown-content z-30 mt-1 w-52 rounded-2xl border border-base-300/60 bg-base-100 p-2 shadow-lg"
				>
					<li>
						<button
							type="button"
							class="min-h-11"
							disabled={bulkBusy}
							onclick={() => bulk('ignore')}
						>
							<Icon name="circle-minus" size={16} /> Ignore all pending
						</button>
					</li>
				</ul>
			</div>
		{/if}
	{/snippet}
</AppBar>

<main class="flex flex-col gap-4 px-4 pt-2">
	{#if !data.page}
		<NoHousehold />
	{:else}
		<div class="flex gap-2 overflow-x-auto pb-1" role="radiogroup" aria-label="Filter by status">
			{#each CHIPS as chip (chip.value)}
				<a
					href="/inbox?status={chip.value}"
					role="radio"
					aria-checked={data.status === chip.value}
					class="btn min-h-10 shrink-0 rounded-full px-4 btn-sm {data.status === chip.value
						? 'btn-neutral'
						: 'border border-base-300 btn-ghost'}"
					data-sveltekit-noscroll
				>
					{chip.label}
					{#if chip.value === 'pending' && pending > 0}
						<span
							class="badge badge-sm {data.status === chip.value
								? 'border-neutral-content/30 badge-neutral'
								: 'badge-ghost'} tabular"
							data-testid="pending-count">{pending}</span
						>
					{/if}
				</a>
			{/each}
		</div>

		{#if data.status === 'pending' && suggestedCount > 0}
			<button
				type="button"
				class="btn min-h-12 rounded-full btn-secondary"
				disabled={bulkBusy}
				onclick={() => bulk('confirm-suggested')}
				data-testid="confirm-all"
			>
				{#if bulkBusy}<span class="loading loading-sm loading-spinner"></span>{:else}<Icon
						name="check-check"
						size={18}
					/>{/if}
				Confirm all suggested ({suggestedCount})
			</button>
		{/if}

		{#if items.length === 0}
			{#if data.status === 'pending'}
				<EmptyState
					icon="inbox"
					title="Nothing to review"
					body="Transactions from a linked bank or an imported CSV wait here until one of you confirms, matches or ignores them."
				>
					<div class="mt-3 flex flex-wrap justify-center gap-2">
						<a href="/settings/banks" class="btn min-h-11 rounded-full px-5 btn-primary"
							><Icon name="landmark" size={16} /> Linked banks</a
						>
						<a
							href="/imports/csv"
							class="btn min-h-11 rounded-full border border-base-300 btn-ghost px-5"
							><Icon name="file-spreadsheet" size={16} /> Import CSV</a
						>
					</div>
				</EmptyState>
			{:else}
				<p class="py-8 text-center text-sm text-base-content/60">
					No {data.status} transactions yet.
				</p>
			{/if}
		{:else}
			{#each groups as g (g.day)}
				<section aria-labelledby="inbox-{g.day}">
					<h2
						id="inbox-{g.day}"
						class="mb-1 px-1 text-sm font-semibold tracking-wide text-base-content/70 uppercase"
					>
						{dayHeading(g.day)}
					</h2>
					<ul
						class="card divide-y divide-base-300/60 border border-base-300/60 bg-base-100 px-4 shadow-sm"
					>
						{#each g.items as item (item.id)}
							<InboxRow
								{item}
								categories={data.categories}
								busy={isBusy(item.id)}
								{onConfirm}
								{onIgnore}
								{onRestore}
								{onPick}
								onOpen={(i) => onPick(i)}
							/>
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

<TransactionSheet
	bind:open={sheet}
	item={selected}
	{initial}
	categories={data.categories}
	onchange={onSheetChange}
/>
