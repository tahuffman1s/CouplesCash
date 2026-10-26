<script lang="ts">
	import { enhance } from '$app/forms';
	import { invalidateAll } from '$app/navigation';
	import type { BillPayment, InboxItem, Occurrence } from '#lib/types.ts';
	import BottomSheet from '#lib/components/BottomSheet.svelte';
	import TransactionSheet from '#lib/components/TransactionSheet.svelte';
	import { apiFetch, ClientApiError } from '#lib/utils/client.ts';
	import { signedAmount } from '#lib/utils/labels.ts';
	import { formatVariance, varianceTone } from '#lib/utils/week.ts';
	import { itemFromPayment, paymentSource } from '#lib/utils/transactions.ts';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import BillForm from '#lib/components/BillForm.svelte';
	import OccurrenceRow from '#lib/components/OccurrenceRow.svelte';
	import { FREQUENCY_LABEL, KIND_LABEL } from '#lib/utils/labels.ts';
	import { formatMoney } from '#lib/utils/money.ts';
	import { formatDate, today } from '#lib/utils/dates.ts';
	import { occurrenceKey, optimisticToggle, togglePaid } from '#lib/utils/occurrences.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data, form }: PageProps = $props();

	let editing = $state(false);
	let submitting = $state(false);
	let confirmDelete = $state(false);
	let occurrences = $derived<Occurrence[]>(data.occurrences);
	let busy = $state<string | null>(null);

	const item = $derived(data.item);
	const category = $derived(data.categories.find((c) => c.id === item.categoryId));
	const account = $derived(data.accounts.find((a) => a.id === item.accountId));

	async function onToggle(occ: Occurrence) {
		const key = occurrenceKey(occ);
		const i = occurrences.findIndex((o) => occurrenceKey(o) === key);
		if (i < 0 || busy) return;
		const previous = occurrences[i];
		occurrences = occurrences.with(i, optimisticToggle(occ, data.user?.id ?? ''));
		busy = key;
		try {
			occurrences = occurrences.with(i, await togglePaid(occ));
			void invalidateAll();
		} catch {
			occurrences = occurrences.with(i, previous);
			toasts.show('Could not update.', 'error');
		} finally {
			busy = null;
		}
	}

	$effect(() => {
		if (form?.saved) {
			editing = false;
			toasts.show('Saved', 'success');
		}
	});
	$effect(() => {
		if (data.created) toasts.show(`${data.item.name} added`, 'success');
	});

	// --- v5: Payments + "Link a transaction"
	const payments = $derived<BillPayment[] | null>(data.payments);
	let linkOpen = $state(false);
	let linking = $state<BillPayment | null>(null);
	let linkCandidates = $state<InboxItem[]>([]);
	let linkLoading = $state(false);
	let linkBusy = $state(false);

	async function openLink(p: BillPayment) {
		linking = p;
		linkOpen = true;
		linkCandidates = [];
		linkLoading = true;
		try {
			linkCandidates = await apiFetch<InboxItem[]>(
				`/recurring/${item.id}/occurrences/${p.dueDate}/candidates?days=10`
			);
		} catch {
			linkCandidates = [];
			toasts.show('Could not load transactions.', 'error');
		} finally {
			linkLoading = false;
		}
	}
	async function link(tx: InboxItem) {
		const p = linking;
		if (!p || linkBusy) return;
		linkBusy = true;
		try {
			await apiFetch<Occurrence>(`/recurring/${item.id}/occurrences/${p.dueDate}/link`, {
				method: 'POST',
				body: JSON.stringify({ transactionId: tx.id })
			});
			linkOpen = false;
			toasts.show(`${item.name} · ${formatDate(p.dueDate)} paid from the bank`, 'success');
			await invalidateAll();
		} catch (e) {
			toasts.show(
				e instanceof ClientApiError && (e.status === 400 || e.status === 409)
					? e.problem.detail || e.problem.title
					: 'Could not link. Check your connection.',
				'error'
			);
		} finally {
			linkBusy = false;
		}
	}

	// tapping a bank payment opens the shared transaction sheet (Unmatch, match elsewhere…)
	let txOpen = $state(false);
	let txItem = $state<InboxItem | null>(null);
	let txDirty = $state(false);
	function openPayment(p: BillPayment) {
		const row = itemFromPayment(item, p);
		if (!row) return;
		txItem = row;
		txOpen = true;
	}
	$effect(() => {
		if (!txOpen && txDirty) {
			txDirty = false;
			void invalidateAll();
		}
	});
	const varianceClass = (cents: number | null) =>
		varianceTone(cents, item.kind) === 'success'
			? 'bg-success/15 text-success'
			: 'bg-error/15 text-error';
</script>

<svelte:head><title>{item.name} · CouplesCash</title></svelte:head>

<AppBar
	title={item.name}
	subtitle="{KIND_LABEL[item.kind]} · {FREQUENCY_LABEL[item.frequency]}"
	back="/bills"
>
	{#snippet action()}
		<button
			type="button"
			class="btn btn-circle min-h-11 min-w-11 btn-ghost btn-sm"
			aria-label={editing ? 'Stop editing' : 'Edit'}
			aria-pressed={editing}
			onclick={() => (editing = !editing)}
		>
			<Icon name={editing ? 'x' : 'pencil'} size={20} />
		</button>
	{/snippet}
</AppBar>

<main class="flex flex-col gap-4 px-4 pt-2">
	{#if !editing}
		<section class="card border border-base-300/60 bg-base-100 shadow-sm">
			<div class="card-body gap-3 p-5">
				<div class="flex items-center gap-3">
					<span
						class="flex h-12 w-12 items-center justify-center rounded-2xl text-white"
						style:background={category?.color ?? 'var(--color-neutral)'}
					>
						<Icon
							name={category?.icon ?? (item.kind === 'income' ? 'banknote' : 'receipt')}
							size={24}
						/>
					</span>
					<div>
						<p
							class="text-3xl font-bold tracking-tight tabular {item.kind === 'income'
								? 'text-success'
								: ''}"
						>
							{formatMoney(item.amountCents)}
						</p>
						<p class="text-xs text-base-content/60">
							{FREQUENCY_LABEL[item.frequency]}
							{#if item.frequency !== 'monthly' && item.frequency !== 'once'}
								· ≈ {formatMoney(item.monthlyEquivalentCents)}/mo
							{/if}
						</p>
					</div>
				</div>
				<dl class="grid grid-cols-2 gap-x-4 gap-y-2 text-sm">
					<dt class="text-base-content/60">Next due</dt>
					<dd class="font-medium">{item.nextDueDate ? formatDate(item.nextDueDate) : 'Ended'}</dd>
					<dt class="text-base-content/60">Category</dt>
					<dd class="font-medium">{category?.name ?? 'None'}</dd>
					<dt class="text-base-content/60">Account</dt>
					<dd class="font-medium">{account?.name ?? 'Any'}</dd>
					<dt class="text-base-content/60">Auto-pay</dt>
					<dd class="font-medium">{item.autoPay ? 'Yes' : 'No'}</dd>
					{#if item.endDate}
						<dt class="text-base-content/60">Ends</dt>
						<dd class="font-medium">{formatDate(item.endDate)}</dd>
					{/if}
				</dl>
				{#if item.url}
					<a
						href={item.url}
						target="_blank"
						rel="noopener noreferrer"
						class="flex link items-center gap-1 text-sm link-primary"
					>
						<Icon name="external-link" size={14} /> Open website
					</a>
				{/if}
				{#if item.notes}<p class="text-sm whitespace-pre-line text-base-content/70">
						{item.notes}
					</p>{/if}
			</div>
		</section>

		{#if payments}
			<section
				class="card border border-base-300/60 bg-base-100 shadow-sm"
				aria-labelledby="payments"
				data-testid="payments"
			>
				<div class="card-body gap-1 p-5">
					<h2 id="payments" class="font-semibold">Payments</h2>
					{#if payments.length === 0}
						<p class="py-3 text-sm text-base-content/60">No payments yet.</p>
					{:else}
						<ul class="divide-y divide-base-300/60">
							{#each payments as p (p.dueDate)}
								{@const variance = p.paid
									? formatVariance(p.varianceCents, (c) => formatMoney(c))
									: null}
								<li
									class="flex items-center gap-3 py-2.5"
									data-testid="payment-row"
									data-date={p.dueDate}
									data-paid={p.paid ? 'true' : 'false'}
								>
									<div class="min-w-0 flex-1">
										<p class="flex items-center gap-1.5 text-sm font-medium">
											{formatDate(p.dueDate)}
											{#if !p.paid}
												<span
													class="badge border-0 px-1.5 badge-xs text-[0.65rem] {p.dueDate < today()
														? 'bg-error/15 text-error'
														: 'bg-base-200 text-base-content/70'}"
													>{p.dueDate < today() ? 'Unpaid' : 'Upcoming'}</span
												>
											{/if}
										</p>
										{#if p.paid}
											{#if p.transaction}
												<button
													type="button"
													class="flex max-w-full items-center gap-1 truncate text-left text-xs text-info"
													data-testid="payment-source"
													onclick={() => openPayment(p)}
												>
													<Icon name="landmark" size={12} />
													<span class="truncate">{paymentSource(p)}</span>
												</button>
											{:else}
												<p class="text-xs text-base-content/60" data-testid="payment-source">
													{paymentSource(p)}
												</p>
											{/if}
										{:else}
											<p class="text-xs text-base-content/60">
												Planned {formatMoney(p.plannedCents)}
											</p>
										{/if}
									</div>
									{#if p.paid}
										<span class="flex shrink-0 flex-col items-end">
											<span class="text-sm font-semibold tabular" data-testid="payment-amount"
												>{formatMoney(p.paid.amountCents)}</span
											>
											{#if variance}
												<span
													class="rounded-full px-1.5 text-[0.65rem] leading-4 font-medium tabular {varianceClass(
														p.varianceCents
													)}"
													data-testid="variance-pill">{variance}</span
												>
											{:else}
												<span class="text-[0.65rem] text-base-content/50"
													>of {formatMoney(p.plannedCents)}</span
												>
											{/if}
										</span>
									{:else}
										<button
											type="button"
											class="btn min-h-11 rounded-full border border-base-300 btn-ghost px-3 btn-sm"
											aria-label="Link a transaction to {formatDate(p.dueDate)}"
											onclick={() => openLink(p)}
										>
											<Icon name="link" size={16} /> Link a transaction
										</button>
									{/if}
								</li>
							{/each}
						</ul>
					{/if}
				</div>
			</section>
		{/if}

		<section class="card border border-base-300/60 bg-base-100 shadow-sm" aria-labelledby="occ">
			<div class="card-body gap-1 p-5">
				<h2 id="occ" class="font-semibold">Recent & upcoming</h2>
				{#if occurrences.length === 0}
					<p class="py-3 text-sm text-base-content/60">No occurrences in the next two months.</p>
				{:else}
					<ul class="divide-y divide-base-300/60">
						{#each occurrences as occ (occurrenceKey(occ))}
							<OccurrenceRow {occ} {onToggle} busy={busy === occurrenceKey(occ)} />
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
				<BillForm
					{item}
					categories={data.categories}
					accounts={data.accounts}
					values={form?.values}
					error={form?.error}
				/>
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
				<h2 class="font-semibold">Remove</h2>
				<p class="text-sm text-base-content/65">
					Removes it from the calendar and totals for both of you.
				</p>
				{#if confirmDelete}
					<div class="flex gap-2">
						<button class="btn min-h-11 flex-1 rounded-full btn-error">Yes, remove</button>
						<button
							type="button"
							class="btn min-h-11 flex-1 rounded-full btn-ghost"
							onclick={() => (confirmDelete = false)}>Cancel</button
						>
					</div>
				{:else}
					<button
						type="button"
						class="btn min-h-11 rounded-full btn-outline btn-error"
						onclick={() => (confirmDelete = true)}
					>
						<Icon name="trash" size={18} /> Remove
					</button>
				{/if}
			</div>
		</form>
	{/if}
</main>

<BottomSheet
	bind:open={linkOpen}
	title={linking ? `Link a transaction · ${formatDate(linking.dueDate)}` : 'Link a transaction'}
>
	{#if linking}
		<p class="mb-2 text-xs text-base-content/60">
			Unmatched bank rows within 10 days of {formatDate(linking.dueDate)}, best match first
		</p>
		{#if linkLoading}
			<div class="h-14 w-full skeleton rounded-xl"></div>
		{:else if linkCandidates.length === 0}
			<p class="py-4 text-center text-sm text-base-content/60" data-testid="link-empty">
				Nothing from the bank fits around then.
			</p>
		{:else}
			<ul class="divide-y divide-base-300/60" aria-label="Transactions to link">
				{#each linkCandidates as tx (tx.id)}
					<li>
						<button
							type="button"
							class="flex min-h-14 w-full items-center gap-3 py-2 text-left"
							disabled={linkBusy}
							data-testid="link-candidate"
							onclick={() => link(tx)}
						>
							<span class="min-w-0 flex-1">
								<span class="block truncate font-medium">{tx.payee || tx.description}</span>
								<span class="block truncate text-xs text-base-content/60"
									>{tx.accountName ?? 'No account'} · {formatDate(tx.postedOn)}{tx.pending
										? ' · Pending'
										: ''}</span
								>
							</span>
							<span class="shrink-0 font-semibold tabular"
								>{signedAmount(tx.amountCents, formatMoney)}</span
							>
						</button>
					</li>
				{/each}
			</ul>
		{/if}
	{/if}
</BottomSheet>

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
