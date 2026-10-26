<script lang="ts">
	import { untrack } from 'svelte';
	import type { RecurringItem } from '#lib/types.ts';
	import { apiFetch } from '#lib/utils/client.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';
	import Icon from './Icon.svelte';

	/**
	 * v6 "Bank shows it as": the names on your statement, as chips. Each chip posts as a hidden
	 * `bankName`; whatever is still typed posts as `bankNameDraft` (so it works without JS too).
	 * Learned names (from confirmed matches) are listed with a "learned" label and can be forgotten
	 * right away (`DELETE /recurring/{id}/bank-names/learned/{index}`).
	 */
	let {
		names = [],
		learned = [],
		itemId = null,
		placeholder = 'NETFLIX.COM'
	}: {
		names?: string[];
		learned?: string[];
		/** set on an existing item so learned names can be forgotten */
		itemId?: string | null;
		placeholder?: string;
	} = $props();

	let chips = $state<string[]>(untrack(() => [...names]));
	let learnedList = $state<string[]>(untrack(() => [...learned]));
	let draft = $state('');
	let busy = $state<number | null>(null);

	function add() {
		const parts = draft
			.split(',')
			.map((s) => s.trim().replace(/\s+/g, ' '))
			.filter((s) => s.length >= 2);
		for (const p of parts.map((s) => s.slice(0, 80)))
			if (chips.length < 10 && !chips.some((c) => c.toLowerCase() === p.toLowerCase()))
				chips = [...chips, p];
		draft = '';
	}
	function onKey(e: KeyboardEvent) {
		if (e.key === 'Enter' || e.key === ',') {
			e.preventDefault();
			add();
		} else if (e.key === 'Backspace' && !draft && chips.length) {
			chips = chips.slice(0, -1);
		}
	}
	async function forget(index: number) {
		if (!itemId || busy !== null) return;
		const name = learnedList[index];
		busy = index;
		try {
			const item = await apiFetch<RecurringItem>(
				`/recurring/${itemId}/bank-names/learned/${index}`,
				{ method: 'DELETE' }
			);
			learnedList = item.bankNamesLearned ?? learnedList.filter((_, i) => i !== index);
			toasts.show(`Forgot "${name}"`, 'success');
		} catch {
			toasts.show('Could not forget that name.', 'error');
		} finally {
			busy = null;
		}
	}
</script>

<div class="form-control" data-testid="bank-names">
	<span class="label-text mb-1 text-sm font-medium" id="bank-names-label"
		>Bank shows it as <span class="text-base-content/50">(optional)</span></span
	>
	<div
		class="flex min-h-12 flex-wrap items-center gap-1.5 rounded-[var(--radius-field)] border border-base-content/20 bg-base-100 px-2 py-1.5 focus-within:outline-2 focus-within:outline-primary"
	>
		{#each chips as c, i (c)}
			<span
				class="badge h-auto min-h-8 gap-1 rounded-full border-0 bg-base-200 py-1 pr-0.5 pl-2.5 text-xs font-medium"
				data-testid="bank-name-chip"
			>
				{c}
				<input type="hidden" name="bankName" value={c} />
				<button
					type="button"
					class="btn btn-circle min-h-8 min-w-8 btn-ghost btn-xs"
					aria-label="Remove {c}"
					onclick={() => (chips = chips.filter((_, j) => j !== i))}
				>
					<Icon name="x" size={12} />
				</button>
			</span>
		{/each}
		<input
			type="text"
			name="bankNameDraft"
			class="min-h-9 min-w-24 flex-1 bg-transparent px-1 text-sm outline-none"
			autocapitalize="characters"
			maxlength="80"
			{placeholder}
			aria-labelledby="bank-names-label"
			bind:value={draft}
			onkeydown={onKey}
			onblur={() => draft.trim() && add()}
		/>
	</div>
	{#if learnedList.length}
		<div class="mt-2 flex flex-wrap items-center gap-1.5" data-testid="bank-names-learned">
			{#each learnedList as l, i (`${i}:${l}`)}
				<span
					class="badge h-auto min-h-8 gap-1 rounded-full border border-dashed border-base-300 bg-transparent py-1 pr-0.5 pl-2.5 text-xs"
					data-testid="learned-chip"
				>
					<span class="font-medium">{l}</span>
					<span class="text-[0.65rem] text-base-content/55 uppercase">learned</span>
					{#if itemId}
						<button
							type="button"
							class="btn btn-circle min-h-8 min-w-8 btn-ghost btn-xs"
							aria-label="Forget learned name {l}"
							disabled={busy !== null}
							onclick={() => forget(i)}
						>
							<Icon name="x" size={12} />
						</button>
					{/if}
				</span>
			{/each}
		</div>
	{/if}
	<span class="label-text-alt mt-1 text-base-content/60">
		The names on your statement. Press Enter after each one; names from confirmed matches are
		learned automatically.
	</span>
</div>
