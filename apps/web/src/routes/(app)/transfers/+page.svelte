<script lang="ts">
	import { invalidateAll } from '$app/navigation';
	import type {
		AccountTransactionsPage,
		InboxItem,
		TransferPair,
		TransferReport
	} from '#lib/types.ts';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import NoHousehold from '#lib/components/NoHousehold.svelte';
	import TransactionSheet, { type SheetSection } from '#lib/components/TransactionSheet.svelte';
	import NetBars from '#lib/charts/NetBars.svelte';
	import { apiFetch, ClientApiError } from '#lib/utils/client.ts';
	import { formatMoney } from '#lib/utils/money.ts';
	import { addMonths, dayHeading, formatDate, formatMonth, today } from '#lib/utils/dates.ts';
	import { signedAmount } from '#lib/utils/labels.ts';
	import { flowLabel, itemFromPairSide, pairsByDate } from '#lib/utils/transactions.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data }: PageProps = $props();

	const report = $derived<TransferReport | null>(data.report);
	const first = $derived(`${data.month}-01`);
	const isCurrent = $derived(data.month === today().slice(0, 7));
	const days = $derived(report ? pairsByDate(report.pairs) : []);
	const empty = $derived(!!report && report.pairs.length === 0 && report.oneSided.length === 0);

	// --- the shared transaction sheet
	let sheet = $state(false);
	let selected = $state<InboxItem | null>(null);
	let initial = $state<SheetSection | undefined>(undefined);
	let dirty = $state(false);
	let opening = $state<string | null>(null);

	/** Loads the full row (description, payee…) from its account's transfers; falls back to the pair. */
	async function openSide(pair: TransferPair, side: 'from' | 'to') {
		const s = pair[side];
		opening = s.transactionId;
		let row: InboxItem = itemFromPairSide(pair, side);
		try {
			if (s.accountId) {
				const page = await apiFetch<AccountTransactionsPage>(
					`/accounts/${s.accountId}/transactions?filter=transfers&month=${pair.date.slice(0, 7)}&limit=100`
				);
				row = page.items.find((t) => t.id === s.transactionId) ?? row;
			}
		} catch {
			// keep the minimal row
		} finally {
			opening = null;
		}
		initial = null;
		selected = row;
		sheet = true;
	}
	function openItem(item: InboxItem, section: SheetSection = null) {
		initial = section;
		selected = item;
		sheet = true;
	}
	function onSheetChange(next: InboxItem) {
		selected = next;
		dirty = true;
	}
	$effect(() => {
		if (!sheet && dirty) {
			dirty = false;
			void invalidateAll();
		}
	});

	let busy = $state<string | null>(null);
	async function notTransfer(item: InboxItem) {
		if (busy) return;
		busy = item.id;
		try {
			await apiFetch<InboxItem>(`/inbox/${item.id}/unmatch`, { method: 'POST' });
			toasts.show('Back in To review', 'success');
			await invalidateAll();
		} catch (e) {
			toasts.show(
				e instanceof ClientApiError && e.problem.detail
					? e.problem.detail
					: 'Could not update. Check your connection.',
				'error'
			);
		} finally {
			busy = null;
		}
	}
</script>

<svelte:head><title>Transfers · CouplesCash</title></svelte:head>

<AppBar title="Transfers" back="/accounts" subtitle="Money moved between your own accounts" />

<main class="flex flex-col gap-4 px-4 pt-2">
	{#if !data.household}
		<NoHousehold />
	{:else}
		<div class="flex items-center justify-between">
			<a
				href="?month={addMonths(first, -1).slice(0, 7)}"
				class="btn btn-circle min-h-11 min-w-11 btn-ghost"
				aria-label="Previous month"
			>
				<Icon name="chevron-left" size={22} />
			</a>
			<h2 class="font-semibold" data-testid="transfers-month">{formatMonth(data.month)}</h2>
			<a
				href="?month={addMonths(first, 1).slice(0, 7)}"
				class="btn btn-circle min-h-11 min-w-11 btn-ghost {isCurrent ? 'btn-disabled' : ''}"
				aria-label="Next month"
				aria-disabled={isCurrent}
			>
				<Icon name="chevron-right" size={22} />
			</a>
		</div>

		{#if !report}
			<p class="py-6 text-center text-sm text-base-content/60">Could not load transfers.</p>
		{:else if empty}
			<EmptyState
				icon="arrow-left-right"
				title="No transfers this month"
				body="Moving money between your own accounts shows up here once a linked bank or a CSV import has both sides. Transfers never count as spending, budgets or income."
			/>
		{:else}
			<section
				class="card border-0 bg-gradient-to-br from-secondary to-secondary/80 text-secondary-content shadow-md"
				aria-labelledby="moved"
			>
				<div class="card-body gap-1 p-5">
					<p id="moved" class="text-xs font-medium tracking-wide uppercase opacity-80">
						Moved between accounts
					</p>
					<p class="text-4xl font-bold tracking-tight tabular" data-testid="transfers-total">
						{formatMoney(report.totalCents)}
					</p>
					<p class="text-xs opacity-80">
						{report.pairs.length} paired · {report.oneSided.length} one-sided · not counted as spending
					</p>
				</div>
			</section>

			{#if report.flows.length > 0}
				<section aria-labelledby="flows">
					<h2
						id="flows"
						class="mb-2 px-1 text-sm font-semibold tracking-wide text-base-content/70 uppercase"
					>
						Flows
					</h2>
					<ul class="flex flex-col gap-2">
						{#each report.flows as f (`${f.fromAccountId}:${f.toAccountId}:${f.fromName}:${f.toName}`)}
							<li
								class="card border border-base-300/60 bg-base-100 shadow-sm"
								data-testid="transfer-flow"
							>
								<div class="card-body flex-row items-center gap-3 p-4">
									<span
										class="flex h-10 w-10 shrink-0 items-center justify-center rounded-2xl bg-info/15 text-info"
									>
										<Icon name="arrow-left-right" size={18} />
									</span>
									<p class="min-w-0 flex-1 text-sm">
										<span class="block truncate font-medium"
											>{flowLabel(f)} · {formatMoney(f.totalCents)}</span
										>
										<span class="block text-xs text-base-content/60"
											>{f.count} transfer{f.count === 1 ? '' : 's'}</span
										>
									</p>
								</div>
							</li>
						{/each}
					</ul>
				</section>
			{/if}

			{#if report.netByAccount.length > 0}
				<section class="card border border-base-300/60 bg-base-100 shadow-sm" aria-labelledby="net">
					<div class="card-body gap-3 p-5">
						<div>
							<h2 id="net" class="font-semibold">Net by account</h2>
							<p class="text-xs text-base-content/60">What each account sent and received</p>
						</div>
						<NetBars rows={report.netByAccount} />
					</div>
				</section>
			{/if}

			{#if days.length > 0}
				<section aria-labelledby="pairs">
					<h2
						id="pairs"
						class="mb-2 px-1 text-sm font-semibold tracking-wide text-base-content/70 uppercase"
					>
						Pairs
					</h2>
					<div class="flex flex-col gap-3">
						{#each days as d (d.day)}
							<div>
								<h3 class="mb-1 px-1 text-xs font-semibold text-base-content/60 uppercase">
									{dayHeading(d.day)}
								</h3>
								<ul
									class="card divide-y divide-base-300/60 border border-base-300/60 bg-base-100 shadow-sm"
								>
									{#each d.pairs as pair (pair.id)}
										<li
											class="grid grid-cols-[1fr_auto_1fr] items-center"
											data-testid="transfer-pair"
										>
											<button
												type="button"
												class="flex min-h-14 flex-col items-start px-4 py-2 text-left"
												aria-label="Open {pair.from.name} side, out {formatMoney(pair.amountCents)}"
												disabled={opening !== null}
												onclick={() => openSide(pair, 'from')}
											>
												<span class="truncate text-sm font-medium">{pair.from.name}</span>
												<span class="text-xs tabular"
													>{signedAmount(-Math.abs(pair.amountCents), formatMoney)}</span
												>
											</button>
											<span class="flex flex-col items-center text-base-content/50">
												<Icon name="arrow-right" size={16} />
												{#if pair.auto}<span class="text-[0.6rem] uppercase">auto</span>{/if}
											</span>
											<button
												type="button"
												class="flex min-h-14 flex-col items-end px-4 py-2 text-right"
												aria-label="Open {pair.to.name} side, in {formatMoney(pair.amountCents)}"
												disabled={opening !== null}
												onclick={() => openSide(pair, 'to')}
											>
												<span class="truncate text-sm font-medium">{pair.to.name}</span>
												<span class="text-xs text-success tabular"
													>{signedAmount(Math.abs(pair.amountCents), formatMoney)}</span
												>
											</button>
										</li>
									{/each}
								</ul>
							</div>
						{/each}
					</div>
				</section>
			{/if}

			{#if report.oneSided.length > 0}
				<section aria-labelledby="onesided">
					<h2
						id="onesided"
						class="mb-1 px-1 text-sm font-semibold tracking-wide text-base-content/70 uppercase"
					>
						One-sided
					</h2>
					<p class="mb-2 px-1 text-xs text-base-content/60">
						The other account isn't linked, or its side hasn't arrived yet.
					</p>
					<ul
						class="card divide-y divide-base-300/60 border border-base-300/60 bg-base-100 px-4 shadow-sm"
					>
						{#each report.oneSided as t (t.id)}
							<li class="flex flex-col gap-2 py-3" data-testid="one-sided">
								<button
									type="button"
									class="flex items-start gap-3 text-left"
									aria-label="Open {t.payee || t.description}"
									onclick={() => openItem(t)}
								>
									<span class="min-w-0 flex-1">
										<span class="block truncate font-medium">{t.payee || t.description}</span>
										<span class="block truncate text-xs text-base-content/60"
											>{t.amountCents < 0
												? `${t.accountName ?? 'No account'} → Other account`
												: `Other account → ${t.accountName ?? 'No account'}`} · {formatDate(
												t.postedOn
											)}{t.transfer?.auto ? ' · auto' : ''}</span
										>
									</span>
									<span class="shrink-0 font-semibold tabular"
										>{signedAmount(t.amountCents, formatMoney)}</span
									>
								</button>
								<div class="flex flex-wrap justify-end gap-2">
									<button
										type="button"
										class="btn min-h-11 rounded-full border border-base-300 btn-ghost px-4 btn-sm"
										disabled={busy === t.id}
										onclick={() => notTransfer(t)}
									>
										Not a transfer
									</button>
									<button
										type="button"
										class="btn min-h-11 rounded-full px-4 btn-primary btn-sm"
										onclick={() => openItem(t, 'transfer')}
									>
										<Icon name="search" size={16} /> Find the other side
									</button>
								</div>
							</li>
						{/each}
					</ul>
				</section>
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
