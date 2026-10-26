<script lang="ts">
	import type { Occurrence } from '#lib/types.ts';
	import { formatMoney } from '#lib/utils/money.ts';
	import { relativeDay, formatDate, today } from '#lib/utils/dates.ts';
	import { formatVariance, varianceTone } from '#lib/utils/week.ts';
	import Icon from './Icon.svelte';

	let {
		occ,
		onToggle,
		showDate = true,
		showStatus = false,
		busy = false,
		onBank
	}: {
		occ: Occurrence;
		onToggle?: (occ: Occurrence) => void;
		showDate?: boolean;
		/** v4: render the Planned / Cleared / Overdue chip from `occ.status` (week view) */
		showStatus?: boolean;
		busy?: boolean;
		/** v5: open the bank transaction that paid this occurrence (the shared transaction sheet) */
		onBank?: (occ: Occurrence) => void;
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
	const STATUS_CLASS = {
		planned: 'bg-base-200 text-base-content/70',
		cleared: 'bg-success/15 text-success',
		overdue: 'bg-error/15 text-error'
	} as const;
	const STATUS_LABEL = { planned: 'Planned', cleared: 'Cleared', overdue: 'Overdue' } as const;
</script>

<li class="flex items-center gap-3 py-2.5" data-testid="occurrence-row" data-status={status}>
	<span class="h-10 w-1.5 shrink-0 rounded-full" style:background={colour} aria-hidden="true"
	></span>
	<a href="/bills/{occ.itemId}" class="min-w-0 flex-1 rounded-lg">
		<p class="flex items-center gap-1.5">
			<span class="truncate font-medium {occ.paid ? 'text-base-content/50 line-through' : ''}">
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
						status
					]}"
					data-testid="status-chip"
				>
					{STATUS_LABEL[status]}
				</span>
			{/if}
		</p>
		<p class="truncate text-xs text-base-content/60">
			{#if autoPaid}
				<span class="inline-flex items-center gap-1 font-medium text-secondary">
					<Icon name="bot" size={12} />
					{fromBank ? 'Matched from bank' : 'Auto-paid'}
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
		<button
			type="button"
			class="btn min-h-11 shrink-0 gap-1 rounded-full border-0 bg-info/15 px-2.5 text-[0.7rem] font-medium text-info btn-sm"
			aria-label="Open the bank transaction that paid {occ.name}"
			data-testid="from-bank"
			onclick={() => onBank(occ)}
		>
			<Icon name="landmark" size={12} strokeWidth={2.5} /> Bank
		</button>
	{/if}
	<span class="flex shrink-0 flex-col items-end">
		<span
			class="text-sm font-semibold tabular {occ.kind === 'income'
				? 'text-success'
				: occ.paid
					? 'text-base-content/50'
					: ''}"
			data-testid="occurrence-amount"
		>
			{occ.kind === 'income' ? '+' : ''}{formatMoney(occ.paid?.amountCents ?? occ.amountCents)}
		</span>
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
				class="btn min-h-11 rounded-full border border-base-300 btn-ghost px-3 btn-sm"
				aria-label="{fromBank ? 'Undo bank match for' : 'Undo auto-pay for'} {occ.name}"
				disabled={busy}
				onclick={() => onToggle(occ)}
			>
				<Icon name="undo-2" size={16} /> Undo
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
