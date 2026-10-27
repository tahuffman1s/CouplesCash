<script lang="ts" module>
	export type SheetSection = 'match' | 'expense' | 'transfer' | 'bill' | null;
</script>

<script lang="ts">
	import type {
		AccountTransaction,
		Category,
		Frequency,
		FromTransactionResult,
		InboxCandidates,
		InboxItem,
		Kind,
		MatchCandidate
	} from '#lib/types.ts';
	import { FREQUENCIES } from '#lib/types.ts';
	import { invalidateAll } from '$app/navigation';
	import BottomSheet from './BottomSheet.svelte';
	import CategoryGrid from './CategoryGrid.svelte';
	import Icon from './Icon.svelte';
	import { apiFetch, ClientApiError } from '#lib/utils/client.ts';
	import { formatMoney } from '#lib/utils/money.ts';
	import { formatDate } from '#lib/utils/dates.ts';
	import { FREQUENCY_LABEL, KIND_ICON, KIND_LABEL, signedAmount } from '#lib/utils/labels.ts';
	import {
		billNameFor,
		candidateSelectable,
		isTransfer,
		kindForAmount,
		matchedTo,
		scoreBar,
		txState
	} from '#lib/utils/transactions.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	type Row = InboxItem | AccountTransaction;

	let {
		open = $bindable(false),
		item,
		categories,
		initial = undefined,
		onchange
	}: {
		open?: boolean;
		item: Row | null;
		categories: Category[];
		/** action expanded when the sheet opens; default: the category grid for money out to review */
		initial?: SheetSection;
		/** called with every optimistic and confirmed version of the row so lists update in place */
		onchange?: (next: InboxItem) => void;
	} = $props();

	// writable copy; resets when the parent hands over a new version of the row
	let current = $derived<Row | null>(item);
	let section = $state<SheetSection>(null);
	let busy = $state(false);
	let candidates = $state<InboxCandidates | null>(null);
	let candidatesFor = $state<string | null>(null);
	let candidatesLoading = $state(false);
	let candidatesError = $state(false);
	let created = $state<{ id: string; name: string } | null>(null);

	// bill mini-form
	let billName = $state('');
	let billKind = $state<Kind>('bill');
	let billFrequency = $state<Frequency>('monthly');

	const manual = $derived(
		!!current && (('kind' in current && current.kind === 'manual') || current.source === 'manual')
	);
	// added by hand, waiting for the bank's copy: it can pay a bill (match / unlink) or be deleted
	const added = $derived(current?.source === 'added');
	const outflow = $derived((current?.amountCents ?? 0) < 0);
	const info = $derived(current ? txState(current) : null);
	const title = $derived(current ? current.payee || current.description : 'Transaction');
	const subline = $derived.by(() => {
		if (!current) return '';
		const parts: string[] = [];
		if (current.payee && current.description !== current.payee) parts.push(current.description);
		if (current.memo) parts.push(current.memo);
		return parts.join(' · ');
	});
	const sourceLabel = $derived(
		!current
			? ''
			: manual
				? 'Manual'
				: added
					? 'Added by you'
					: current.source === 'csv'
						? 'CSV'
						: 'Bank'
	);
	const undoLabel = $derived.by(() => {
		if (!current || current.status === 'pending') return null;
		if (isTransfer(current)) return 'Not a transfer';
		if (current.status === 'ignored') return 'Restore';
		return 'Unmatch';
	});
	const kindOptions = $derived<Kind[]>(outflow ? ['bill', 'subscription'] : ['income']);

	// reset per opened row
	let openedFor = $state<string | null>(null);
	$effect(() => {
		if (!open) {
			openedFor = null;
			return;
		}
		const row = item;
		if (!row || openedFor === row.id) return;
		openedFor = row.id;
		created = null;
		billName = billNameFor(row);
		billKind = kindForAmount(row.amountCents);
		billFrequency = 'monthly';
		const wanted =
			initial !== undefined
				? initial
				: row.source === 'added'
					? row.ignoreReason || matchedTo(row)
						? null
						: 'match'
					: row.status === 'pending' && row.amountCents < 0
						? 'expense'
						: row.status === 'pending'
							? 'match'
							: null;
		section = wanted;
		if (wanted === 'match' || wanted === 'transfer') void loadCandidates(row.id);
	});

	async function loadCandidates(id: string) {
		if (candidatesFor === id && candidates) return;
		candidatesFor = id;
		candidates = null;
		candidatesError = false;
		candidatesLoading = true;
		try {
			const res = await apiFetch<InboxCandidates>(`/inbox/${id}/candidates?days=30`);
			if (candidatesFor === id) candidates = res;
		} catch {
			if (candidatesFor === id) candidatesError = true;
		} finally {
			candidatesLoading = false;
		}
	}

	function toggle(next: Exclude<SheetSection, null>) {
		section = section === next ? null : next;
		if (current && (section === 'match' || section === 'transfer')) void loadCandidates(current.id);
	}

	function problemText(e: unknown): string {
		if (e instanceof ClientApiError && (e.status === 400 || e.status === 409 || e.status === 404))
			return e.problem.detail || e.problem.title || "That doesn't fit this transaction.";
		return 'Could not update. Check your connection.';
	}

	/** Optimistic update → request → server version; reverts and explains on failure. */
	async function act(
		optimistic: Partial<InboxItem> | null,
		request: (row: Row) => Promise<InboxItem>,
		success: (next: InboxItem) => string,
		close = true
	): Promise<InboxItem | null> {
		const row = current;
		if (!row || busy) return null;
		busy = true;
		if (optimistic) {
			current = { ...row, ...optimistic };
			onchange?.(current);
		}
		try {
			const next = await request(row);
			current = { ...row, ...next };
			onchange?.(current);
			candidates = null;
			candidatesFor = null;
			toasts.show(success(next), 'success');
			if (close) open = false;
			return next;
		} catch (e) {
			current = row;
			onchange?.(row);
			toasts.show(problemText(e), 'error');
			return null;
		} finally {
			busy = false;
		}
	}

	async function removeAdded() {
		const row = current;
		if (!row || busy) return;
		busy = true;
		try {
			await apiFetch<void>(`/transactions/${row.id}`, { method: 'DELETE' });
			toasts.show(`Removed ${row.description}`, 'success');
			open = false;
			await invalidateAll();
		} catch (e) {
			toasts.show(problemText(e), 'error');
		} finally {
			busy = false;
		}
	}

	const post = (path: string, body?: unknown) =>
		apiFetch<InboxItem>(path, {
			method: 'POST',
			body: body === undefined ? undefined : JSON.stringify(body)
		});

	function pickCategory(categoryId: string | null, name: string) {
		void act(
			{
				status: 'confirmed',
				ignoreReason: null,
				transfer: null,
				match: null,
				expense: { expenseId: '', categoryId, categoryName: categoryId ? name : null },
				confirmed: {
					expenseId: '',
					itemId: null,
					dueDate: null,
					by: null,
					byName: null,
					at: null,
					auto: false,
					categoryName: categoryId ? name : null
				}
			},
			async (row) => {
				// confirm only takes pending rows: reverse whatever was done first
				if (row.status !== 'pending') await post(`/inbox/${row.id}/unmatch`);
				return post(`/inbox/${row.id}/confirm`, { as: 'expense', categoryId });
			},
			() => (categoryId ? `Expense added in ${name}` : 'Expense added')
		);
	}

	function pickOccurrence(c: MatchCandidate) {
		void act(
			{
				status: 'confirmed',
				ignoreReason: null,
				transfer: null,
				expense: null,
				match: { itemId: c.itemId, itemName: c.itemName, itemKind: c.kind, dueDate: c.dueDate },
				confirmed: {
					expenseId: null,
					itemId: c.itemId,
					dueDate: c.dueDate,
					by: null,
					byName: null,
					at: null,
					auto: false,
					itemName: c.itemName
				}
			},
			(row) => post(`/inbox/${row.id}/match`, { itemId: c.itemId, dueDate: c.dueDate }),
			() => `Matched to ${c.itemName}`
		);
	}

	function pickPeer(peer: InboxItem | null) {
		void act(
			{
				status: 'ignored',
				ignoreReason: 'transfer',
				confirmed: null,
				match: null,
				expense: null,
				transfer: {
					peerId: peer?.id ?? null,
					peerAccountId: peer?.accountId ?? null,
					peerAccountName: peer?.accountName ?? null,
					auto: false
				}
			},
			(row) => post(`/inbox/${row.id}/transfer`, { peerId: peer?.id ?? null }),
			() =>
				peer?.accountName ? `Marked as a transfer with ${peer.accountName}` : 'Marked as a transfer'
		);
	}

	function ignore() {
		void act(
			{ status: 'ignored', ignoreReason: 'manual' },
			(row) => post(`/inbox/${row.id}/ignore`),
			() => 'Ignored'
		);
	}

	function unmatch() {
		const label = undoLabel;
		if (current?.source === 'added') {
			const name = matchedTo(current)?.itemName ?? 'the bill';
			void act(
				{ status: 'confirmed', confirmed: null, match: null },
				(row) => post(`/inbox/${row.id}/unmatch`),
				() => `Unlinked from ${name}`
			);
			return;
		}
		void act(
			{
				status: 'pending',
				confirmed: null,
				ignoreReason: null,
				transfer: null,
				match: null,
				expense: null
			},
			(row) => post(`/inbox/${row.id}/unmatch`),
			() => (label === 'Not a transfer' ? 'Not a transfer any more' : 'Back in To review')
		);
	}

	async function createBill(e: SubmitEvent) {
		e.preventDefault();
		const row = current;
		if (!row || busy) return;
		const name = billName.trim();
		if (!name) {
			toasts.show('Give the bill a name.', 'error');
			return;
		}
		busy = true;
		try {
			const res = await apiFetch<FromTransactionResult>('/recurring/from-transaction', {
				method: 'POST',
				body: JSON.stringify({
					transactionId: row.id,
					name,
					kind: billKind,
					frequency: billFrequency
				})
			});
			current = { ...row, ...res.transaction };
			onchange?.(current);
			created = { id: res.item.id, name: res.item.name };
			section = null;
			toasts.show(`${res.item.name} created and matched`, 'success');
		} catch (err) {
			toasts.show(problemText(err), 'error');
		} finally {
			busy = false;
		}
	}

	const occurrences = $derived(candidates?.occurrences ?? []);
	const peers = $derived(candidates?.transferPeers ?? []);
	const STATE_CLASS: Record<string, string> = {
		review: 'bg-warning/15 text-pending',
		expense: 'bg-success/15 text-success',
		paid: 'bg-success/15 text-success',
		manual: 'bg-base-200 text-base-content/80',
		transfer: 'bg-info/15 text-info',
		ignored: 'bg-base-200 text-base-content/70',
		dropped: 'bg-base-200 text-base-content/70',
		superseded: 'bg-base-200 text-base-content/70'
	};
	const actionClass =
		'flex min-h-12 w-full items-center gap-3 rounded-xl px-2 text-left text-sm font-medium hover:bg-base-200 disabled:opacity-50';
</script>

{#snippet actionButton(key: Exclude<SheetSection, null>, icon: string, label: string, hint: string)}
	<button
		type="button"
		class={actionClass}
		aria-expanded={section === key}
		disabled={busy}
		onclick={() => toggle(key)}
	>
		<span
			class="flex h-9 w-9 shrink-0 items-center justify-center rounded-xl bg-base-200 text-base-content/80"
		>
			<Icon name={icon} size={18} />
		</span>
		<span class="min-w-0 flex-1">
			<span class="block">{label}</span>
			<span class="block truncate text-xs font-normal text-base-content/60">{hint}</span>
		</span>
		<Icon
			name={section === key ? 'chevron-up' : 'chevron-down'}
			size={16}
			class="text-base-content/50"
		/>
	</button>
{/snippet}

{#snippet matchAction()}
	<li>
		{@render actionButton(
			'match',
			'receipt',
			'Match to a bill or income',
			outflow ? 'Mark a bill or subscription paid with this' : 'Mark an income received'
		)}
		{#if section === 'match'}
			<div class="px-2 pt-1 pb-3" data-testid="match-candidates">
				{#if candidatesLoading}
					<div class="h-14 w-full skeleton rounded-xl"></div>
				{:else if candidatesError}
					<p class="py-3 text-center text-sm text-base-content/60">Could not load candidates.</p>
				{:else if occurrences.length === 0}
					<p class="py-3 text-center text-sm text-base-content/60">
						Nothing {outflow ? 'due' : 'expected'} within 30 days of {formatDate(
							current?.postedOn ?? ''
						)}.
						{#if !added}
							<button type="button" class="link link-primary" onclick={() => toggle('bill')}
								>Create a bill from this</button
							>
						{/if}
					</p>
				{:else}
					<ul class="divide-y divide-base-300/60" aria-label="Bills and income to match">
						{#each occurrences as c (`${c.itemId}:${c.dueDate}`)}
							{@const bar = scoreBar(c.score)}
							{@const selectable = candidateSelectable(c)}
							<li>
								<button
									type="button"
									class="flex min-h-14 w-full items-center gap-3 py-2 text-left disabled:opacity-50"
									disabled={!selectable || busy}
									aria-label="Match to {c.itemName}, due {formatDate(c.dueDate)}{c.paid &&
									!c.paidByThis
										? ', already paid'
										: ''}"
									data-testid="match-candidate"
									onclick={() => pickOccurrence(c)}
								>
									<span
										class="flex h-9 w-9 shrink-0 items-center justify-center rounded-xl bg-base-200"
									>
										<Icon name={KIND_ICON[c.kind]} size={16} />
									</span>
									<span class="min-w-0 flex-1">
										<span class="flex items-center gap-1.5">
											<span class="truncate font-medium">{c.itemName}</span>
											{#if c.paidByThis}
												<span class="badge badge-xs badge-success">this one</span>
											{:else if c.paid}
												<span class="badge badge-ghost badge-xs">already paid</span>
											{/if}
										</span>
										<span class="block text-xs text-base-content/60"
											>Due {formatDate(c.dueDate)}</span
										>
										<span
											class="mt-1 block h-1.5 w-full max-w-32 overflow-hidden rounded-full bg-base-300/60"
											role="meter"
											aria-valuemin="0"
											aria-valuemax="100"
											aria-valuenow={bar.percent}
											aria-label={bar.label}
											title={bar.label}
										>
											<span
												class="block h-full rounded-full {bar.tone === 'strong'
													? 'bg-success'
													: bar.tone === 'good'
														? 'bg-secondary'
														: 'bg-base-content/30'}"
												style:width="{bar.percent}%"
											></span>
										</span>
									</span>
									<span class="shrink-0 text-sm font-semibold tabular"
										>{formatMoney(c.amountCents)}</span
									>
								</button>
							</li>
						{/each}
					</ul>
				{/if}
			</div>
		{/if}
	</li>
{/snippet}

<BottomSheet bind:open {title}>
	{#if current && info}
		<div class="flex flex-col gap-4" data-testid="transaction-sheet" data-id={current.id}>
			<!-- header -->
			<div class="flex flex-col gap-1">
				<div class="flex items-baseline justify-between gap-3">
					<p
						class="text-3xl font-bold tracking-tight tabular {current.amountCents < 0
							? ''
							: 'text-success'}"
						data-testid="sheet-amount"
					>
						{signedAmount(current.amountCents, formatMoney)}
					</p>
					<div class="flex shrink-0 items-center gap-1">
						{#if current.pending}
							<span
								class="badge border-0 bg-warning/20 badge-sm font-medium text-pending"
								data-testid="sheet-pending">Pending</span
							>
						{/if}
						<span
							class="badge gap-1 border-0 bg-base-200 badge-sm font-medium text-base-content/70"
						>
							<Icon
								name={manual
									? 'pencil'
									: current.source === 'csv'
										? 'file-spreadsheet'
										: 'landmark'}
								size={11}
							/>
							{sourceLabel}
						</span>
					</div>
				</div>
				<p class="text-sm text-base-content/70">
					{current.accountName ?? 'No account'} · {formatDate(current.postedOn)}
				</p>
				{#if current.payee && current.payee !== title}
					<p class="text-xs text-base-content/60">Payee: {current.payee}</p>
				{/if}
				{#if subline}<p class="text-xs break-words text-base-content/60">{subline}</p>{/if}
				<p
					class="mt-1 inline-flex w-fit items-center gap-1.5 rounded-full px-2.5 py-1 text-xs font-medium {STATE_CLASS[
						info.kind
					]}"
					data-testid="sheet-state"
				>
					<Icon
						name={info.kind === 'review'
							? 'inbox'
							: info.kind === 'transfer'
								? 'arrow-left-right'
								: info.kind === 'ignored' || info.kind === 'dropped'
									? 'circle-minus'
									: 'circle-check'}
						size={13}
					/>
					{info.label}
				</p>
			</div>

			{#if created}
				<div class="alert text-sm alert-success" role="status">
					<Icon name="circle-check" size={18} />
					<span class="flex-1">{created.name} will match itself from now on.</span>
					<a href="/bills/{created.id}" class="link font-semibold" data-testid="open-new-bill"
						>Open {created.name}</a
					>
				</div>
			{/if}

			{#if added}
				<p class="text-sm text-base-content/70">
					{current.ignoreReason === 'superseded'
						? 'The bank showed its own copy, so this one no longer counts.'
						: matchedTo(current)
							? `You added this and it pays ${matchedTo(current)?.itemName}. When the bank shows its own copy, that one takes its place and pays the bill.`
							: 'You added this. It counts in This week until the bank shows its own copy, which then takes its place. If it paid a bill, match it below so it isn’t counted twice.'}
				</p>
				{#if current.ignoreReason !== 'superseded'}
					<ul class="flex flex-col gap-1" aria-label="Actions">
						{@render matchAction()}
						{#if matchedTo(current)}
							<li>
								<button
									type="button"
									class={actionClass}
									disabled={busy}
									onclick={unmatch}
									data-testid="unlink-added"
								>
									<span
										class="flex h-9 w-9 shrink-0 items-center justify-center rounded-xl bg-base-200 text-base-content/80"
									>
										<Icon name="undo-2" size={18} />
									</span>
									<span class="min-w-0 flex-1">
										<span class="block">Unlink from {matchedTo(current)?.itemName}</span>
										<span class="block text-xs font-normal text-base-content/60"
											>The bill goes back to unpaid</span
										>
									</span>
								</button>
							</li>
						{/if}
					</ul>
					<button
						type="button"
						class="btn min-h-12 rounded-full border-error/40 btn-outline text-error"
						disabled={busy}
						onclick={removeAdded}
						data-testid="delete-added"
					>
						<Icon name="trash" size={18} /> Delete
					</button>
				{/if}
			{:else if manual}
				<a
					href="/expenses/{current.id}"
					class="btn min-h-12 rounded-full btn-primary"
					data-testid="edit-expense"
				>
					<Icon name="pencil" size={18} /> Edit expense
				</a>
			{:else}
				<ul class="flex flex-col gap-1" aria-label="Actions">
					<!-- Match to a bill or income -->
					{@render matchAction()}

					<!-- Confirm as expense (money out only) -->
					{#if outflow}
						<li>
							{@render actionButton(
								'expense',
								'tag',
								'Confirm as expense',
								'Pick a category; the balance already includes it'
							)}
							{#if section === 'expense'}
								<div class="px-2 pt-1 pb-3">
									<CategoryGrid
										{categories}
										disabled={busy}
										onpick={pickCategory}
										selectedId={current.status === 'confirmed' && current.expense
											? current.expense.categoryId
											: undefined}
									/>
								</div>
							{/if}
						</li>
					{/if}

					<!-- Mark as transfer -->
					<li>
						{@render actionButton(
							'transfer',
							'arrow-left-right',
							isTransfer(current) && current.status !== 'pending'
								? 'Change the other side'
								: 'Mark as transfer',
							'Money moved between your own accounts'
						)}
						{#if section === 'transfer'}
							<div class="px-2 pt-1 pb-3" data-testid="transfer-peers">
								{#if candidatesLoading}
									<div class="h-14 w-full skeleton rounded-xl"></div>
								{:else}
									<ul class="divide-y divide-base-300/60" aria-label="Other side of the transfer">
										{#each peers as peer (peer.id)}
											<li>
												<button
													type="button"
													class="flex min-h-14 w-full items-center gap-3 py-2 text-left"
													disabled={busy}
													aria-label="Transfer with {peer.accountName ??
														'unknown account'} · {signedAmount(peer.amountCents, formatMoney)}"
													data-testid="transfer-peer"
													onclick={() => pickPeer(peer)}
												>
													<span
														class="flex h-9 w-9 shrink-0 items-center justify-center rounded-xl bg-info/15 text-info"
													>
														<Icon name="landmark" size={16} />
													</span>
													<span class="min-w-0 flex-1">
														<span class="block truncate font-medium"
															>{peer.accountName ?? 'Unknown account'}</span
														>
														<span class="block truncate text-xs text-base-content/60"
															>{peer.description} · {formatDate(peer.postedOn)}</span
														>
													</span>
													<span class="shrink-0 text-sm font-semibold tabular"
														>{signedAmount(peer.amountCents, formatMoney)}</span
													>
												</button>
											</li>
										{/each}
										<li>
											<button
												type="button"
												class="flex min-h-14 w-full items-center gap-3 py-2 text-left"
												disabled={busy}
												data-testid="transfer-other"
												onclick={() => pickPeer(null)}
											>
												<span
													class="flex h-9 w-9 shrink-0 items-center justify-center rounded-xl bg-base-200"
												>
													<Icon name="arrow-right" size={16} />
												</span>
												<span class="min-w-0 flex-1">
													<span class="block font-medium">Other account (not linked)</span>
													<span class="block text-xs text-base-content/60"
														>A brokerage, someone else's bank… we'll remember this payee</span
													>
												</span>
											</button>
										</li>
									</ul>
								{/if}
							</div>
						{/if}
					</li>

					<!-- Create a bill from this -->
					<li>
						{@render actionButton(
							'bill',
							'list-plus',
							'Create a bill from this',
							'Future months match themselves'
						)}
						{#if section === 'bill'}
							<form class="flex flex-col gap-3 px-2 pt-1 pb-3" onsubmit={createBill}>
								<label class="form-control">
									<span class="label-text mb-1 text-sm font-medium">Bill name</span>
									<input
										type="text"
										class="input-bordered input min-h-12 w-full"
										maxlength="80"
										required
										bind:value={billName}
									/>
								</label>
								<div class="grid grid-cols-2 gap-2">
									<label class="form-control">
										<span class="label-text mb-1 text-sm font-medium">Kind</span>
										<select class="select-bordered select min-h-12 w-full" bind:value={billKind}>
											{#each kindOptions as k (k)}
												<option value={k}>{KIND_LABEL[k]}</option>
											{/each}
										</select>
									</label>
									<label class="form-control">
										<span class="label-text mb-1 text-sm font-medium">Frequency</span>
										<select
											class="select-bordered select min-h-12 w-full"
											bind:value={billFrequency}
										>
											{#each FREQUENCIES.filter((f) => f !== 'once') as f (f)}
												<option value={f}>{FREQUENCY_LABEL[f]}</option>
											{/each}
										</select>
									</label>
								</div>
								<p class="text-xs text-base-content/60">
									{formatMoney(Math.abs(current.amountCents))} starting {formatDate(
										current.postedOn
									)}; this transaction pays the first one.
								</p>
								<button class="btn min-h-12 rounded-full btn-primary" disabled={busy}>
									{#if busy}<span class="loading loading-sm loading-spinner"></span>{/if}
									Create bill
								</button>
							</form>
						{/if}
					</li>

					{#if current.status === 'pending'}
						<li>
							<button type="button" class={actionClass} disabled={busy} onclick={ignore}>
								<span
									class="flex h-9 w-9 shrink-0 items-center justify-center rounded-xl bg-base-200 text-base-content/80"
								>
									<Icon name="circle-minus" size={18} />
								</span>
								<span class="flex-1">Ignore</span>
							</button>
						</li>
					{/if}
					{#if undoLabel}
						<li>
							<button type="button" class={actionClass} disabled={busy} onclick={unmatch}>
								<span
									class="flex h-9 w-9 shrink-0 items-center justify-center rounded-xl bg-base-200 text-base-content/80"
								>
									<Icon name="undo-2" size={18} />
								</span>
								<span class="min-w-0 flex-1">
									<span class="block">{undoLabel}</span>
									<span class="block text-xs font-normal text-base-content/60"
										>Back to To review; undoes what was done</span
									>
								</span>
							</button>
						</li>
					{/if}
				</ul>
			{/if}
		</div>
	{/if}
</BottomSheet>
