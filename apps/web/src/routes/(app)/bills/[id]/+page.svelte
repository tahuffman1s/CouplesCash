<script lang="ts">
	import { enhance } from '$app/forms';
	import { invalidateAll } from '$app/navigation';
	import type { Occurrence } from '#lib/types.ts';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import BillForm from '#lib/components/BillForm.svelte';
	import OccurrenceRow from '#lib/components/OccurrenceRow.svelte';
	import { FREQUENCY_LABEL, KIND_LABEL } from '#lib/utils/labels.ts';
	import { formatMoney } from '#lib/utils/money.ts';
	import { formatDate } from '#lib/utils/dates.ts';
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
