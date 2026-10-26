<script lang="ts">
	import { invalidateAll } from '$app/navigation';
	import type { BillSuggestion, RecurringItem } from '#lib/types.ts';
	import { apiFetch, ClientApiError } from '#lib/utils/client.ts';
	import { formatDate } from '#lib/utils/dates.ts';
	import { cadenceLabel } from '#lib/utils/quickadd.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';
	import Amount from './Amount.svelte';
	import Icon from './Icon.svelte';

	/**
	 * v6 "Found bills": regular payments the matcher spotted in imported transactions. Add creates
	 * the item with the bank name and matches the past rows it was found from; Not a bill hides it
	 * for good.
	 */
	let { suggestions }: { suggestions: BillSuggestion[] } = $props();

	let list = $derived<BillSuggestion[]>(suggestions);
	let busy = $state<string | null>(null);

	async function accept(s: BillSuggestion) {
		if (busy) return;
		busy = s.id;
		try {
			const r = await apiFetch<{ item: RecurringItem; matched: number }>(
				`/bill-suggestions/${s.id}/accept`,
				{ method: 'POST', body: JSON.stringify({}) }
			);
			list = list.filter((x) => x.id !== s.id);
			toasts.show(
				`${r.item.name} added${r.matched ? ` · ${r.matched} past payment${r.matched === 1 ? '' : 's'} matched` : ''}`,
				'success'
			);
			void invalidateAll();
		} catch (e) {
			if (e instanceof ClientApiError && e.status === 409) {
				list = list.filter((x) => x.id !== s.id);
				toasts.show(`${s.name} was already handled`, 'info');
				void invalidateAll();
			} else toasts.show('Could not add it. Check your connection.', 'error');
		} finally {
			busy = null;
		}
	}
	async function dismiss(s: BillSuggestion) {
		if (busy) return;
		busy = s.id;
		const before = list;
		list = list.filter((x) => x.id !== s.id);
		try {
			await apiFetch(`/bill-suggestions/${s.id}/dismiss`, { method: 'POST' });
			toasts.show(`Got it, ${s.name} is not a bill`, 'info');
		} catch (e) {
			// 409: someone already added it – it is gone either way
			if (!(e instanceof ClientApiError && e.status === 409)) {
				list = before;
				toasts.show('Could not update. Check your connection.', 'error');
			}
		} finally {
			busy = null;
		}
	}
</script>

{#if list.length}
	<section
		class="card border border-secondary/30 bg-secondary/5 shadow-sm"
		aria-labelledby="found-bills"
		data-testid="found-bills"
	>
		<div class="card-body gap-2 p-4">
			<h2 id="found-bills" class="flex items-center gap-2 font-semibold">
				<Icon name="sparkles" size={18} class="text-secondary" />
				We found {list.length} regular payment{list.length === 1 ? '' : 's'}
			</h2>
			<ul class="divide-y divide-base-300/60">
				{#each list as s (s.id)}
					<li class="flex flex-wrap items-center gap-x-3 gap-y-2 py-2.5" data-testid="found-bill">
						<div class="min-w-[10rem] flex-1">
							<p class="truncate font-medium">{s.name}</p>
							<p class="text-xs text-base-content/65">
								<Amount cents={(s.kind === 'income' ? 1 : -1) * s.amountCents} />
								{s.amountVaries ? '(varies) ' : ''}{cadenceLabel(s.frequency).toLowerCase()} ·
								<span class="whitespace-nowrap">next {formatDate(s.nextDue)}</span>
							</p>
						</div>
						<div class="flex shrink-0 gap-1.5">
							<button
								type="button"
								class="btn min-h-11 rounded-full px-4 btn-secondary btn-sm"
								disabled={busy === s.id}
								aria-label="Add {s.name}"
								onclick={() => accept(s)}
							>
								{#if busy === s.id}<span class="loading loading-xs loading-spinner"></span>{/if}
								Add
							</button>
							<button
								type="button"
								class="btn min-h-11 rounded-full border border-base-300 btn-ghost px-3 btn-sm"
								disabled={busy === s.id}
								aria-label="{s.name} is not a bill"
								onclick={() => dismiss(s)}
							>
								Not a bill
							</button>
						</div>
					</li>
				{/each}
			</ul>
		</div>
	</section>
{/if}
