<script lang="ts">
	import { invalidateAll } from '$app/navigation';
	import type {
		BulkResult,
		CalendarResponse,
		InboxItem,
		InboxPage,
		InboxStatus,
		Occurrence
	} from '#lib/types.ts';
	import type { InboxConfirmBody } from '#lib/server/api.ts';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import NoHousehold from '#lib/components/NoHousehold.svelte';
	import BottomSheet from '#lib/components/BottomSheet.svelte';
	import InboxRow from '#lib/components/InboxRow.svelte';
	import { apiFetch, ClientApiError } from '#lib/utils/client.ts';
	import { addDays, dayHeading, formatDate } from '#lib/utils/dates.ts';
	import { formatMoney } from '#lib/utils/money.ts';
	import { KIND_ICON, signedAmount } from '#lib/utils/labels.ts';
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
		if (!s || s.kind === 'transfer') return void onPick(item);
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
			{ status: 'ignored' },
			() => apiFetch<InboxItem>(`/inbox/${item.id}/ignore`, { method: 'POST' }),
			() => 'Ignored',
			-1
		);
	}
	function onRestore(item: InboxItem) {
		void run(
			item,
			{ status: 'pending', confirmed: null },
			() => apiFetch<InboxItem>(`/inbox/${item.id}/restore`, { method: 'POST' }),
			() => 'Back in the inbox',
			+1
		);
	}

	// --- change-suggestion sheet
	let sheet = $state(false);
	let picking = $state<InboxItem | null>(null);
	let occs = $state<Occurrence[]>([]);
	let occsLoading = $state(false);
	let mode = $state<'category' | 'occurrence'>('category');
	const outflow = $derived((picking?.amountCents ?? 0) < 0);
	const matchable = $derived(
		occs.filter((o) => !o.paid && (outflow ? o.kind !== 'income' : o.kind === 'income'))
	);

	async function onPick(item: InboxItem) {
		picking = item;
		mode = item.amountCents < 0 ? 'category' : 'occurrence';
		sheet = true;
		occs = [];
		occsLoading = true;
		try {
			const res = await apiFetch<CalendarResponse>(
				`/calendar?from=${addDays(item.postedOn, -10)}&to=${addDays(item.postedOn, 10)}`
			);
			occs = res.occurrences;
		} catch {
			occs = [];
		} finally {
			occsLoading = false;
		}
	}
	function pickCategory(categoryId: string | null, name: string) {
		const item = picking;
		sheet = false;
		if (!item) return;
		void confirmWith(item, { as: 'expense', categoryId }, `Expense added in ${name}`);
	}
	function pickOccurrence(o: Occurrence) {
		const item = picking;
		sheet = false;
		if (!item) return;
		void confirmWith(
			item,
			{ as: 'occurrence', itemId: o.itemId, dueDate: o.dueDate },
			`${o.name} marked paid`
		);
	}

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
				`/inbox?status=${data.status}&before=${encodeURIComponent(nextBefore)}&limit=50`
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

<BottomSheet
	bind:open={sheet}
	title={picking
		? `${picking.payee || picking.description} · ${signedAmount(picking.amountCents, formatMoney)}`
		: 'Change suggestion'}
>
	{#if picking}
		{#if outflow}
			<div class="mb-3 flex gap-2" role="tablist" aria-label="Confirm as">
				<button
					type="button"
					role="tab"
					aria-selected={mode === 'category'}
					class="btn min-h-10 flex-1 rounded-full btn-sm {mode === 'category'
						? 'btn-neutral'
						: 'border border-base-300 btn-ghost'}"
					onclick={() => (mode = 'category')}
				>
					<Icon name="tag" size={14} /> Expense
				</button>
				<button
					type="button"
					role="tab"
					aria-selected={mode === 'occurrence'}
					class="btn min-h-10 flex-1 rounded-full btn-sm {mode === 'occurrence'
						? 'btn-neutral'
						: 'border border-base-300 btn-ghost'}"
					onclick={() => (mode = 'occurrence')}
				>
					<Icon name="receipt" size={14} /> Match to a bill
				</button>
			</div>
		{:else}
			<p class="mb-3 text-sm text-base-content/70">
				Money in: match it to an expected income, or ignore it.
			</p>
		{/if}

		{#if mode === 'category' && outflow}
			<p class="mb-2 text-xs font-medium text-base-content/60">Pick a category</p>
			<div class="grid grid-cols-3 gap-2" role="group" aria-label="Categories">
				{#each data.categories as c (c.id)}
					<button
						type="button"
						class="flex min-h-16 flex-col items-center justify-center gap-1 rounded-2xl border border-base-300/60 bg-base-100 p-2 text-xs font-medium hover:bg-base-200"
						onclick={() => pickCategory(c.id, c.name)}
					>
						<span
							class="flex h-8 w-8 items-center justify-center rounded-xl text-white"
							style:background={c.color}
						>
							<Icon name={c.icon} size={16} />
						</span>
						<span class="truncate">{c.name}</span>
					</button>
				{/each}
				<button
					type="button"
					class="flex min-h-16 flex-col items-center justify-center gap-1 rounded-2xl border border-dashed border-base-300 p-2 text-xs font-medium text-base-content/70 hover:bg-base-200"
					onclick={() => pickCategory(null, 'no category')}
				>
					<span class="flex h-8 w-8 items-center justify-center rounded-xl bg-base-200">
						<Icon name="tag" size={16} />
					</span>
					No category
				</button>
			</div>
		{:else}
			<p class="mb-2 text-xs font-medium text-base-content/60">
				{outflow ? 'Bills and subscriptions' : 'Income'} due within 10 days of {formatDate(
					picking.postedOn
				)}
			</p>
			{#if occsLoading}
				<div class="h-14 w-full skeleton rounded-xl"></div>
			{:else if matchable.length === 0}
				<p class="py-4 text-center text-sm text-base-content/60">
					Nothing unpaid is due around then.
					<a href="/bills/new" class="link link-primary">Add a bill</a>
				</p>
			{:else}
				<ul class="divide-y divide-base-300/60" aria-label="Matching occurrences">
					{#each matchable as o (`${o.itemId}:${o.dueDate}`)}
						<li>
							<button
								type="button"
								class="flex min-h-14 w-full items-center gap-3 py-2 text-left"
								onclick={() => pickOccurrence(o)}
							>
								<span
									class="flex h-9 w-9 shrink-0 items-center justify-center rounded-xl text-white"
									style:background={o.categoryColor ?? 'var(--color-neutral)'}
								>
									<Icon name={KIND_ICON[o.kind]} size={16} />
								</span>
								<span class="min-w-0 flex-1">
									<span class="block truncate font-medium">{o.name}</span>
									<span class="block text-xs text-base-content/60">Due {formatDate(o.dueDate)}</span
									>
								</span>
								<span class="font-semibold tabular">{formatMoney(o.amountCents)}</span>
							</button>
						</li>
					{/each}
				</ul>
			{/if}
		{/if}
	{/if}
</BottomSheet>
