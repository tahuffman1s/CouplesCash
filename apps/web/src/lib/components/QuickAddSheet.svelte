<script lang="ts" module>
	export type QuickAddForm = {
		quickError?: string;
		text?: string;
		quickAdded?: {
			name: string;
			schedule: string;
			amountCents: number;
			kind: string;
			count: number;
		};
		// the Transaction tab (`?/addTransaction`)
		added?: { description: string; amountCents: number };
		addError?: string;
		addDuplicate?: boolean;
		addValues?: {
			direction: 'out' | 'in';
			amount: string;
			description: string;
			accountId: string;
			postedOn: string;
		};
	};
	/** Narrows a page's `form` (any action) to the quick-add result, if it is one. */
	export function quickAddForm(form: unknown): QuickAddForm | null {
		if (!form || typeof form !== 'object') return null;
		const f = form as QuickAddForm;
		return 'quickAdded' in f || 'quickError' in f || 'added' in f || 'addError' in f ? f : null;
	}
</script>

<script lang="ts">
	import { SvelteURLSearchParams } from 'svelte/reactivity';
	import { enhance } from '$app/forms';
	import type { Account, Category, Kind } from '#lib/types.ts';
	import { KINDS } from '#lib/types.ts';
	import AddTransactionForm from './AddTransactionForm.svelte';
	import BottomSheet from './BottomSheet.svelte';
	import Icon from './Icon.svelte';
	import Amount from './Amount.svelte';
	import { KIND_LABEL } from '#lib/utils/labels.ts';
	import { centsToInput, currencySymbol, formatMoney, parseMoney } from '#lib/utils/money.ts';
	import { today } from '#lib/utils/dates.ts';
	import {
		CADENCES,
		cadenceLabel,
		draftProblems,
		guessCategory,
		parseQuickAdd,
		scheduleText,
		shortDate,
		type Cadence
	} from '#lib/utils/quickadd.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	/**
	 * "+": two tabs. Transaction (the default) adds something the bank hasn't shown yet
	 * (`?/addTransaction`). Bill or one-off is the v6 sentence: one text field, a live preview parsed
	 * by `quickadd.ts`, tappable chips to correct it, Add → `?/quickAdd`; "More options" opens the
	 * full bill form.
	 */
	let {
		open = $bindable(false),
		categories,
		accounts,
		defaultAccountId = null,
		form = null
	}: {
		open?: boolean;
		categories: Category[];
		accounts: Account[];
		defaultAccountId?: string | null;
		form?: QuickAddForm | null;
	} = $props();

	type Field = 'kind' | 'amount' | 'cadence' | 'date' | 'category' | 'account';
	type Overrides = Partial<{
		kind: Kind;
		amountCents: number | null;
		amountVaries: boolean;
		cadence: Cadence;
		anchorDate: string;
		categoryId: string | null;
		accountId: string | null;
		autoPay: boolean;
	}>;

	const EXAMPLES = [
		'netflix 15.49 on the 7th',
		'oil change 60 friday',
		'paycheck 3200 every other friday'
	];

	let mode = $state<'transaction' | 'bill'>('transaction');
	let text = $state('');
	let overrides = $state<Overrides>({});
	let editing = $state<Field | null>(null);
	let amountText = $state('');
	let submitting = $state(false);
	let input = $state<HTMLInputElement | null>(null);
	const todayIso = today();

	const parsed = $derived(parseQuickAdd(text, todayIso));
	const draft = $derived({ ...parsed, ...overrides });
	const categoryIdFor = (name: string | null) =>
		name ? (categories.find((c) => c.name.toLowerCase() === name.toLowerCase())?.id ?? null) : null;
	const categoryId = $derived(
		overrides.categoryId !== undefined
			? overrides.categoryId
			: categoryIdFor(guessCategory(draft.name, draft.kind))
	);
	const accountId = $derived(
		overrides.accountId !== undefined ? overrides.accountId : defaultAccountId
	);
	const category = $derived(categories.find((c) => c.id === categoryId));
	const account = $derived(accounts.find((a) => a.id === accountId));
	const problems = $derived(draftProblems(draft));
	const ready = $derived(text.trim().length > 0 && problems.length === 0);
	const signed = $derived((draft.kind === 'income' ? 1 : -1) * (draft.amountCents ?? 0));
	const moreHref = $derived.by(() => {
		const q = new SvelteURLSearchParams();
		if (draft.name) q.set('name', draft.name);
		q.set('kind', draft.kind);
		if (draft.amountCents) q.set('amount', centsToInput(draft.amountCents));
		const f =
			draft.cadence === 'semimonthly'
				? 'biweekly'
				: draft.cadence === 'semiannual'
					? 'yearly'
					: draft.cadence;
		q.set('frequency', f);
		q.set('anchor', draft.anchorDate);
		return `/bills/new?${q}`;
	});
	function reset() {
		text = '';
		overrides = {};
		editing = null;
	}
	function edit(field: Field) {
		editing = editing === field ? null : field;
		if (editing === 'amount') amountText = draft.amountCents ? centsToInput(draft.amountCents) : '';
	}
	function cycleKind() {
		const i = KINDS.indexOf(draft.kind);
		overrides = { ...overrides, kind: KINDS[(i + 1) % KINDS.length] };
	}
	function setAmount() {
		const cents = parseMoney(amountText);
		overrides = { ...overrides, amountCents: cents !== null && cents > 0 ? cents : null };
	}

	$effect(() => {
		if (!form?.quickAdded) return;
		const a = form.quickAdded;
		toasts.show(
			`${a.name} added · ${a.count > 1 ? `${a.count} items · ` : ''}${a.schedule}`,
			'success'
		);
		open = false;
		reset();
	});
	$effect(() => {
		if (form?.quickError) {
			open = true;
			mode = 'bill';
			if (form.text && !text) text = form.text;
		}
	});
	$effect(() => {
		if (form?.addError) {
			open = true;
			mode = 'transaction';
		}
	});
	$effect(() => {
		if (!form?.added) return;
		const a = form.added;
		toasts.show(`${a.description} added · ${formatMoney(Math.abs(a.amountCents))}`, 'success');
		open = false;
	});
	$effect(() => {
		if (open && mode === 'bill') queueMicrotask(() => input?.focus());
	});
	const txAccountId = $derived(
		accounts.find((a) => a.role === 'bills')?.id ?? defaultAccountId ?? accounts[0]?.id ?? null
	);
	const tab = 'tab min-h-11 flex-1 font-medium';

	const chip =
		'btn h-auto min-h-11 gap-1 rounded-full border border-base-300 bg-base-100 px-3 py-1 text-xs font-medium normal-case shadow-none';
</script>

<BottomSheet bind:open title="Add something">
	<div class="tabs tabs-box mb-3 w-full" role="tablist" aria-label="What to add">
		<button
			type="button"
			role="tab"
			class="{tab} {mode === 'transaction' ? 'tab-active' : ''}"
			aria-selected={mode === 'transaction'}
			onclick={() => (mode = 'transaction')}
			data-testid="quick-tab-transaction">Transaction</button
		>
		<button
			type="button"
			role="tab"
			class="{tab} {mode === 'bill' ? 'tab-active' : ''}"
			aria-selected={mode === 'bill'}
			onclick={() => (mode = 'bill')}
			data-testid="quick-tab-bill">Bill or one-off</button
		>
	</div>
	{#if mode === 'transaction'}
		<AddTransactionForm {accounts} accountId={txAccountId} {form} />
	{:else}
		<form
			method="POST"
			action="?/quickAdd"
			class="flex flex-col gap-3"
			data-testid="quick-add-form"
			use:enhance={() => {
				submitting = true;
				return async ({ update }) => {
					await update({ reset: false });
					submitting = false;
				};
			}}
		>
			<label class="form-control">
				<span class="label-text mb-1 text-sm font-medium">Say it in a few words</span>
				<input
					bind:this={input}
					name="text"
					type="text"
					autocomplete="off"
					autocapitalize="off"
					enterkeyhint="done"
					maxlength="160"
					class="input-bordered input min-h-12 w-full text-base"
					placeholder="netflix 15.49 on the 7th"
					aria-describedby="quick-add-hint"
					data-testid="quick-add-input"
					bind:value={text}
				/>
			</label>

			{#if form?.quickError}
				<div class="alert text-sm alert-error" role="alert">{form.quickError}</div>
			{/if}

			{#if !text.trim()}
				<div id="quick-add-hint" class="flex flex-col gap-2">
					<p class="text-xs text-base-content/60">A name, an amount and when. Try one of these:</p>
					<div class="flex flex-wrap gap-2">
						{#each EXAMPLES as ex (ex)}
							<button type="button" class={chip} onclick={() => (text = ex)}>{ex}</button>
						{/each}
					</div>
				</div>
			{:else}
				<!-- live preview -->
				<section
					class="rounded-2xl border border-base-300/70 bg-base-200/50 p-3"
					aria-live="polite"
					aria-label="Preview"
					data-testid="quick-add-preview"
				>
					<div class="flex items-start gap-3">
						<span
							class="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl text-white"
							style:background={category?.color ?? 'var(--color-neutral)'}
						>
							<Icon
								name={category?.icon ??
									(draft.kind === 'income'
										? 'banknote'
										: draft.kind === 'subscription'
											? 'repeat'
											: 'receipt')}
								size={18}
							/>
						</span>
						<div class="min-w-0 flex-1">
							<p class="truncate font-semibold" data-testid="preview-name">
								{draft.name || 'Name?'}
							</p>
							<p class="text-xs text-base-content/65" data-testid="preview-schedule">
								{scheduleText(draft, todayIso)}
							</p>
						</div>
						{#if draft.amountCents}
							<Amount cents={signed} class="font-semibold" testid="preview-amount" />
						{/if}
					</div>

					<div class="mt-3 flex flex-wrap gap-1.5" aria-label="Tap to change">
						<button type="button" class={chip} data-testid="chip-kind" onclick={cycleKind}>
							{KIND_LABEL[draft.kind]}
						</button>
						<button
							type="button"
							class="{chip} {draft.amountCents ? '' : 'border-dashed text-error'}"
							data-testid="chip-amount"
							aria-expanded={editing === 'amount'}
							onclick={() => edit('amount')}
						>
							{draft.amountCents
								? `${draft.amountVaries ? '~' : ''}${formatMoney(draft.amountCents)}`
								: 'Amount?'}
						</button>
						<button
							type="button"
							class={chip}
							data-testid="chip-cadence"
							aria-expanded={editing === 'cadence'}
							onclick={() => edit('cadence')}
						>
							{cadenceLabel(draft.cadence)}
						</button>
						<button
							type="button"
							class={chip}
							data-testid="chip-date"
							aria-expanded={editing === 'date'}
							onclick={() => edit('date')}
						>
							<Icon name="calendar-days" size={12} />
							{shortDate(draft.anchorDate, todayIso)}
						</button>
						<button
							type="button"
							class={chip}
							data-testid="chip-category"
							aria-expanded={editing === 'category'}
							onclick={() => edit('category')}
						>
							{#if category}<span
									class="h-2 w-2 rounded-full"
									style:background={category.color}
									aria-hidden="true"
								></span>{/if}
							{category?.name ?? 'No category'}
						</button>
						<button
							type="button"
							class={chip}
							data-testid="chip-account"
							aria-expanded={editing === 'account'}
							onclick={() => edit('account')}
						>
							<Icon name="landmark" size={12} />
							{account?.name ?? 'Any account'}
						</button>
						{#if draft.kind !== 'income'}
							<button
								type="button"
								class="{chip} {draft.autoPay
									? 'border-secondary/40 bg-secondary/10 text-secondary'
									: ''}"
								aria-pressed={draft.autoPay}
								data-testid="chip-autopay"
								onclick={() => (overrides = { ...overrides, autoPay: !draft.autoPay })}
							>
								<Icon name="zap" size={12} />
								{draft.autoPay ? 'Autopay' : 'Not autopay'}
							</button>
						{/if}
					</div>

					{#if editing === 'amount'}
						<div class="mt-3 flex items-center gap-2">
							<label class="input-bordered input flex min-h-11 flex-1 items-center gap-2">
								<span class="text-base-content/50">{currencySymbol()}</span>
								<input
									type="text"
									inputmode="decimal"
									class="grow tabular"
									aria-label="Amount"
									bind:value={amountText}
									oninput={setAmount}
								/>
							</label>
							<label class="flex min-h-11 cursor-pointer items-center gap-2 text-sm">
								<input
									type="checkbox"
									class="checkbox checkbox-sm"
									checked={draft.amountVaries}
									onchange={(e) =>
										(overrides = { ...overrides, amountVaries: e.currentTarget.checked })}
								/>
								Varies
							</label>
						</div>
					{:else if editing === 'cadence'}
						<select
							class="select-bordered select mt-3 min-h-11 w-full"
							aria-label="How often"
							value={draft.cadence}
							onchange={(e) =>
								(overrides = { ...overrides, cadence: e.currentTarget.value as Cadence })}
						>
							{#each CADENCES as c (c.value)}
								<option value={c.value}>{c.label}</option>
							{/each}
						</select>
					{:else if editing === 'date'}
						<input
							type="date"
							class="input-bordered input mt-3 min-h-11 w-full"
							aria-label={draft.cadence === 'once' ? 'Date' : 'First date'}
							value={draft.anchorDate}
							onchange={(e) =>
								e.currentTarget.value &&
								(overrides = { ...overrides, anchorDate: e.currentTarget.value })}
						/>
					{:else if editing === 'category'}
						<select
							class="select-bordered select mt-3 min-h-11 w-full"
							aria-label="Category"
							value={categoryId ?? ''}
							onchange={(e) =>
								(overrides = { ...overrides, categoryId: e.currentTarget.value || null })}
						>
							<option value="">No category</option>
							{#each categories as c (c.id)}
								<option value={c.id}>{c.name}</option>
							{/each}
						</select>
					{:else if editing === 'account'}
						<select
							class="select-bordered select mt-3 min-h-11 w-full"
							aria-label="Account"
							value={accountId ?? ''}
							onchange={(e) =>
								(overrides = { ...overrides, accountId: e.currentTarget.value || null })}
						>
							<option value="">Any account</option>
							{#each accounts as a (a.id)}
								<option value={a.id}>{a.name}</option>
							{/each}
						</select>
					{/if}

					{#if problems.length}
						<p class="mt-2 text-xs text-error" data-testid="preview-problems">
							{problems.join(' · ')}
						</p>
					{:else if draft.cadence === 'semimonthly' || draft.cadence === 'semiannual'}
						<p class="mt-2 text-xs text-base-content/60">
							Saved as two {draft.cadence === 'semimonthly' ? 'monthly' : 'yearly'} items so each date
							is right.
						</p>
					{/if}
				</section>
			{/if}

			<!-- the corrected preview (ignored by the server when JavaScript is off) -->
			<input type="hidden" name="structured" value="1" />
			<input type="hidden" name="name" value={draft.name} />
			<input type="hidden" name="kind" value={draft.kind} />
			<input type="hidden" name="amountCents" value={draft.amountCents ?? ''} />
			<input type="hidden" name="cadence" value={draft.cadence} />
			<input type="hidden" name="anchorDate" value={draft.anchorDate} />
			<input type="hidden" name="categoryId" value={categoryId ?? ''} />
			<input type="hidden" name="accountId" value={accountId ?? ''} />
			<input type="hidden" name="bankName" value={draft.bankName} />
			{#if draft.autoPay}<input type="hidden" name="autoPay" value="on" />{/if}
			{#if draft.amountVaries}<input type="hidden" name="amountVaries" value="on" />{/if}

			<button
				class="btn min-h-12 rounded-full btn-primary"
				disabled={!ready || submitting}
				data-testid="quick-add-submit"
			>
				{#if submitting}<span class="loading loading-sm loading-spinner"></span>{/if}
				Add
			</button>
			<div class="flex items-center justify-between gap-2 text-sm">
				<a
					href={moreHref}
					class="btn min-h-11 rounded-full btn-ghost btn-sm"
					data-testid="quick-more">More options</a
				>
				<a href="/bills/paste" class="btn min-h-11 rounded-full btn-ghost btn-sm"
					>Paste from notes</a
				>
			</div>
		</form>
	{/if}
</BottomSheet>
