<script lang="ts">
	import { enhance } from '$app/forms';
	import type { Category, Kind } from '#lib/types.ts';
	import { KINDS } from '#lib/types.ts';
	import Icon from './Icon.svelte';
	import { KIND_LABEL } from '#lib/utils/labels.ts';
	import { centsToInput, currencySymbol, formatMoney, parseMoney } from '#lib/utils/money.ts';
	import { today } from '#lib/utils/dates.ts';
	import { guessNoteAccount, parseNotes } from '#lib/utils/notes.ts';
	import {
		CADENCES,
		cadenceLabel,
		guessCategory,
		shortDate,
		toRecurringBodies,
		type Cadence
	} from '#lib/utils/quickadd.ts';

	/**
	 * v7 "Paste your notes": her bill list, one per line, read with the quick-add grammar. A preview
	 * row per line with a checkbox and chips to correct it; lines it can't read stay listed as
	 * skipped. "Add N bills" posts the corrected list (`items` JSON) to `action` → POST /recurring/bulk.
	 */
	let {
		action,
		categories,
		billsAccountId = null,
		savingsAccountId = null,
		error = null
	}: {
		action: string;
		categories: Category[];
		billsAccountId?: string | null;
		savingsAccountId?: string | null;
		error?: string | null;
	} = $props();

	type Fix = Partial<{
		off: boolean;
		kind: Kind;
		amountCents: number;
		cadence: Cadence;
		anchorDate: string;
		accountId: string | null;
	}>;
	const todayIso = today();
	let text = $state('');
	let fixes = $state<Record<number, Fix>>({});
	let editing = $state<{ line: number; field: 'amount' | 'cadence' | 'date' } | null>(null);
	let submitting = $state(false);

	const lines = $derived(parseNotes(text, todayIso));
	const readable = $derived(lines.filter((l) => l.draft));
	const skipped = $derived(lines.filter((l) => l.skip));
	const accountIds = $derived({ billsId: billsAccountId, savingsId: savingsAccountId });
	const rows = $derived(
		readable.map((l) => {
			const f = fixes[l.index] ?? {};
			const draft = { ...l.draft!, ...f };
			const accountId =
				f.accountId !== undefined ? f.accountId : guessNoteAccount(draft.name, accountIds);
			return { line: l, draft, accountId, on: !f.off };
		})
	);
	const chosen = $derived(rows.filter((r) => r.on));
	const categoryIdFor = (name: string | null) =>
		name ? (categories.find((c) => c.name.toLowerCase() === name.toLowerCase())?.id ?? null) : null;
	const payload = $derived(
		JSON.stringify(
			chosen.flatMap((r) =>
				toRecurringBodies(r.draft, {
					categoryId: categoryIdFor(guessCategory(r.draft.name, r.draft.kind)),
					accountId: r.accountId
				}).map((body) => ({ line: r.line.index, body }))
			)
		)
	);

	function fix(index: number, patch: Fix) {
		fixes = { ...fixes, [index]: { ...(fixes[index] ?? {}), ...patch } };
	}
	function toggleEdit(line: number, field: 'amount' | 'cadence' | 'date') {
		editing = editing?.line === line && editing.field === field ? null : { line, field };
	}
	const accountName = (id: string | null) =>
		id && id === savingsAccountId
			? 'Savings'
			: id && id === billsAccountId
				? 'Bills'
				: 'Any account';
	function cycleAccount(index: number, current: string | null) {
		const order = [billsAccountId, savingsAccountId, null].filter((v, i, a) => a.indexOf(v) === i);
		fix(index, { accountId: order[(order.indexOf(current) + 1) % order.length] ?? null });
	}

	const chip =
		'btn h-auto min-h-9 gap-1 rounded-full border border-base-300 bg-base-100 px-2.5 py-0.5 text-xs font-medium normal-case shadow-none';
	const EXAMPLE = 'Rent 1850 1st\nNetflix 15.49 7th\nCar ins 216 every 6 months\nWater ~60 20th';
</script>

<form
	method="POST"
	{action}
	class="flex flex-col gap-3"
	data-testid="notes-import"
	use:enhance={() => {
		submitting = true;
		return async ({ update }) => {
			await update({ reset: false });
			submitting = false;
		};
	}}
>
	<label class="form-control">
		<span class="label-text mb-1 text-sm font-medium">Paste your bill list</span>
		<textarea
			class="textarea-bordered textarea [field-sizing:content] max-h-96 min-h-36 w-full text-base leading-relaxed"
			placeholder={EXAMPLE}
			autocapitalize="off"
			spellcheck="false"
			data-testid="notes-input"
			bind:value={text}></textarea>
		<span class="label-text-alt mt-1 text-base-content/60"
			>One bill per line, the way you wrote it: a name, an amount and when.</span
		>
	</label>

	{#if error}
		<div class="alert text-sm alert-error" role="alert">{error}</div>
	{/if}

	{#if rows.length}
		<ul class="flex flex-col gap-2" aria-label="What we read" data-testid="notes-preview">
			{#each rows as r (r.line.index)}
				{@const i = r.line.index}
				{@const d = r.draft}
				<li
					class="rounded-2xl border border-base-300/70 p-3 {r.on ? '' : 'opacity-55'}"
					data-testid="note-row"
				>
					<div class="flex items-start gap-2">
						<input
							type="checkbox"
							class="checkbox mt-0.5 checkbox-sm"
							checked={r.on}
							aria-label="Add {d.name}"
							onchange={(e) => fix(i, { off: !e.currentTarget.checked })}
						/>
						<div class="min-w-0 flex-1">
							<p class="flex items-baseline gap-2">
								<span class="min-w-0 flex-1 truncate font-medium" data-testid="note-name"
									>{d.name}</span
								>
								<span class="shrink-0 font-mono text-sm tabular" data-testid="note-amount"
									>{d.amountVaries ? '~' : ''}{formatMoney(d.amountCents ?? 0)}</span
								>
							</p>
							<p class="truncate text-xs text-base-content/50">“{r.line.text}”</p>
							<div class="mt-2 flex flex-wrap gap-1" aria-label="Tap to change">
								<button
									type="button"
									class={chip}
									onclick={() =>
										fix(i, { kind: KINDS[(KINDS.indexOf(d.kind) + 1) % KINDS.length] })}
									>{KIND_LABEL[d.kind]}</button
								>
								<button type="button" class={chip} onclick={() => toggleEdit(i, 'amount')}
									>{formatMoney(d.amountCents ?? 0)}</button
								>
								<button type="button" class={chip} onclick={() => toggleEdit(i, 'cadence')}
									>{cadenceLabel(d.cadence)}</button
								>
								<button type="button" class={chip} onclick={() => toggleEdit(i, 'date')}
									><Icon name="calendar-days" size={12} />{shortDate(
										d.anchorDate,
										todayIso
									)}</button
								>
								{#if savingsAccountId || billsAccountId}
									<button
										type="button"
										class="{chip} {r.accountId === savingsAccountId && savingsAccountId
											? 'border-secondary/40 text-secondary'
											: ''}"
										data-testid="note-account"
										onclick={() => cycleAccount(i, r.accountId)}
										><Icon name="landmark" size={12} />{accountName(r.accountId)}</button
									>
								{/if}
							</div>
							{#if editing?.line === i && editing.field === 'amount'}
								<label class="input-bordered input mt-2 flex min-h-11 items-center gap-2">
									<span class="text-base-content/50">{currencySymbol()}</span>
									<input
										type="text"
										inputmode="decimal"
										class="grow tabular"
										aria-label="Amount for {d.name}"
										value={centsToInput(d.amountCents ?? 0)}
										oninput={(e) => {
											const c = parseMoney(e.currentTarget.value);
											if (c && c > 0) fix(i, { amountCents: c });
										}}
									/>
								</label>
							{:else if editing?.line === i && editing.field === 'cadence'}
								<select
									class="select-bordered select mt-2 min-h-11 w-full"
									aria-label="How often for {d.name}"
									value={d.cadence}
									onchange={(e) => fix(i, { cadence: e.currentTarget.value as Cadence })}
								>
									{#each CADENCES as c (c.value)}
										<option value={c.value}>{c.label}</option>
									{/each}
								</select>
							{:else if editing?.line === i && editing.field === 'date'}
								<input
									type="date"
									class="input-bordered input mt-2 min-h-11 w-full"
									aria-label="Date for {d.name}"
									value={d.anchorDate}
									onchange={(e) =>
										e.currentTarget.value && fix(i, { anchorDate: e.currentTarget.value })}
								/>
							{/if}
						</div>
					</div>
				</li>
			{/each}
		</ul>
	{/if}

	{#if skipped.length}
		<section class="rounded-2xl bg-base-200/60 p-3" data-testid="notes-skipped">
			<p class="text-xs font-semibold text-base-content/70">
				Skipped {skipped.length} line{skipped.length === 1 ? '' : 's'} (add them with + if they're bills)
			</p>
			<ul class="mt-1 flex flex-col gap-0.5">
				{#each skipped as s (s.index)}
					<li class="flex gap-2 text-xs" data-testid="note-skipped">
						<span class="min-w-0 flex-1 truncate text-base-content/70">“{s.text}”</span>
						<span class="shrink-0 text-base-content/50">{s.skip}</span>
					</li>
				{/each}
			</ul>
		</section>
	{/if}

	<input type="hidden" name="items" value={payload} />
	<input type="hidden" name="count" value={chosen.length} />
	<button
		class="btn min-h-12 rounded-full btn-primary"
		disabled={chosen.length === 0 || submitting}
		data-testid="notes-submit"
	>
		{#if submitting}<span class="loading loading-sm loading-spinner"></span>{/if}
		{chosen.length === 0
			? 'Add bills'
			: `Add ${chosen.length} bill${chosen.length === 1 ? '' : 's'}`}
	</button>
</form>
