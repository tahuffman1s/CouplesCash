<script lang="ts">
	import type { Occurrence } from '#lib/types.ts';
	import { formatMoney } from '#lib/utils/money.ts';
	import { relativeDay, formatDate, today } from '#lib/utils/dates.ts';
	import { formatVariance, varianceTone } from '#lib/utils/week.ts';
	import Icon from './Icon.svelte';
	import Amount from './Amount.svelte';

	let {
		occ,
		onToggle,
		showDate = true,
		showStatus = false,
		busy = false,
		onBank,
		pending = false
	}: {
		occ: Occurrence;
		onToggle?: (occ: Occurrence) => void;
		showDate?: boolean;
		/** v4: render the Planned / Cleared / Overdue chip from `occ.status` (week view) */
		showStatus?: boolean;
		busy?: boolean;
		/** v5: open the bank transaction that paid this occurrence (the shared transaction sheet) */
		onBank?: (occ: Occurrence) => void;
		/** v6: paid by a bank transaction that is still pending */
		pending?: boolean;
	} = $props();

	const overdue = $derived(!occ.paid && occ.kind !== 'income' && occ.dueDate < today());
	const colour = $derived(occ.categoryColor ?? 'var(--color-base-300)');
	const fromBank = $derived(occ.paid?.source === 'bank');
	const autoPaid = $derived(!!occ.paid?.auto);
	// v4: the API says what the row is; fall back to the local rule for older builds
	const status = $derived(occ.status ?? (occ.paid ? 'cleared' : overdue ? 'overdue' : 'planned'));
	const variance = $derived(
		occ.paid ? formatVariance(occ.varianceCents ?? null, (c) => formatMoney(c)) : null
	);
	const varianceClass = $derived(
		varianceTone(occ.varianceCents ?? null, occ.kind) === 'success'
			? 'bg-success/15 text-success'
			: 'bg-error/15 text-error'
	);
	// v6 vocabulary: Planned · Paid · Late · Pending
	const STATUS_CLASS = {
		planned: 'bg-base-200 text-base-content/70',
		cleared: 'bg-success/15 text-success',
		overdue: 'bg-error/15 text-error',
		pending: 'bg-warning/20 text-pending'
	} as const;
	const STATUS_LABEL = {
		planned: 'Planned',
		cleared: 'Paid',
		overdue: 'Late',
		pending: 'Pending'
	} as const;
	const shown = $derived(pending && occ.paid ? 'pending' : status);
	const signedCents = $derived(
		(occ.kind === 'income' ? 1 : -1) * (occ.paid?.amountCents ?? occ.amountCents)
	);
</script>

<li class="flex items-center gap-3 py-2.5" data-testid="occurrence-row" data-status={status}>
	<span class="h-10 w-1.5 shrink-0 rounded-full" style:background={colour} aria-hidden="true"
	></span>
	<a href="/bills/{occ.itemId}" class="min-w-0 flex-1 rounded-lg">
		<p class="flex flex-wrap items-center gap-x-1.5 gap-y-0.5">
			<span
				class="min-w-[3.5rem] truncate font-medium {occ.paid
					? 'text-base-content/50 line-through'
					: ''}"
			>
				{occ.name}
			</span>
			{#if occ.autoPay && !autoPaid}
				<span
					class="badge shrink-0 gap-0.5 border-0 bg-secondary/15 px-1.5 badge-xs text-[0.65rem] font-medium text-secondary"
				>
					<Icon name="zap" size={10} strokeWidth={2.5} /> Autopay
				</span>
			{/if}
			{#if fromBank && !onBank}
				<span
					class="badge shrink-0 gap-0.5 border-0 bg-info/15 px-1.5 badge-xs text-[0.65rem] font-medium text-info"
					data-testid="from-bank"
				>
					<Icon name="landmark" size={10} strokeWidth={2.5} /> From bank
				</span>
			{/if}
			{#if showStatus}
				<span
					class="badge shrink-0 border-0 px-1.5 badge-xs text-[0.65rem] font-medium {STATUS_CLASS[
						shown
					]}"
					data-testid="status-chip"
				>
					{STATUS_LABEL[shown]}
				</span>
			{/if}
		</p>
		<p class="truncate text-xs text-base-content/60">
			{#if autoPaid}
				<span class="inline-flex items-center gap-1 font-medium text-secondary">
					<Icon name="bot" size={12} />
					{fromBank ? 'Matched' : 'Auto-paid'}
				</span>
				{#if showDate}<span aria-hidden="true"> · </span><span>{formatDate(occ.dueDate)}</span>{/if}
			{:else if showDate}
				<span class={overdue ? 'font-medium text-error' : ''}>{relativeDay(occ.dueDate)}</span>
				<span aria-hidden="true"> · </span>
				<span>{formatDate(occ.dueDate)}</span>
			{:else if occ.categoryName}
				{occ.categoryName}
			{/if}
		</p>
	</a>
	{#if fromBank && onBank}
		<!-- icon-only so a paid row keeps room for its name on a phone (44 px target) -->
		<button
			type="button"
			class="btn min-h-11 w-11 shrink-0 rounded-full border-0 bg-info/15 p-0 text-info btn-sm"
			aria-label="Open the bank transaction that paid {occ.name}"
			title="Bank transaction"
			data-testid="from-bank"
			onclick={() => onBank(occ)}
		>
			<Icon name="landmark" size={16} strokeWidth={2.25} />
		</button>
	{/if}
	<span class="flex shrink-0 flex-col items-end">
		<Amount
			cents={signedCents}
			muted={!!occ.paid}
			class="text-sm font-semibold"
			testid="occurrence-amount"
		/>
		{#if variance}
			<span
				class="rounded-full px-1.5 text-[0.65rem] leading-4 font-medium tabular {varianceClass}"
				data-testid="variance-pill"
				aria-label="{variance} versus the planned {formatMoney(occ.amountCents)}"
			>
				{variance}
			</span>
		{/if}
	</span>
	{#if onToggle}
		{#if autoPaid}
			<button
				type="button"
				class="btn min-h-11 w-11 shrink-0 rounded-full border border-base-300 btn-ghost p-0 btn-sm"
				aria-label="{fromBank ? 'Undo bank match for' : 'Undo auto-pay for'} {occ.name}"
				title="Undo"
				disabled={busy}
				onclick={() => onToggle(occ)}
			>
				<Icon name="undo-2" size={16} />
			</button>
		{:else}
			<button
				type="button"
				class="btn btn-circle min-h-11 min-w-11 border btn-sm {occ.paid
					? 'text-success-content btn-success'
					: 'border-base-300 btn-ghost'}"
				aria-pressed={!!occ.paid}
				aria-label={occ.paid ? `Mark ${occ.name} unpaid` : `Mark ${occ.name} paid`}
				disabled={busy}
				onclick={() => onToggle(occ)}
			>
				<Icon name="check" size={18} strokeWidth={2.5} />
			</button>
		{/if}
	{/if}
</li>
