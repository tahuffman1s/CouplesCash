<script lang="ts">
	import { enhance } from '$app/forms';
	import type { PlanLine } from '#lib/types.ts';
	import Icon from './Icon.svelte';
	import { formatDate } from '#lib/utils/dates.ts';
	import { signedMoney } from '#lib/utils/money.ts';
	import { lineName, lineStatus } from '#lib/utils/plan.ts';

	/**
	 * v7: one line of the plan's expanded rows (a bill, a fun-money transfer, a one-off, the
	 * paycheck). The tick marks it paid / unpaid (`?/paid` on /week); the name opens the bill.
	 */
	let {
		line,
		today,
		tickable = true
	}: { line: PlanLine; today: string; tickable?: boolean } = $props();

	const o = $derived(line.occurrence);
	const status = $derived(lineStatus(line, today));
	const income = $derived(o.kind === 'income');
	let busy = $state(false);
</script>

<li
	class="flex min-h-12 items-center gap-2 py-1.5"
	data-testid="plan-line"
	data-status={status.toLowerCase()}
>
	{#if tickable}
		<form
			method="POST"
			action="?/paid"
			use:enhance={() => {
				busy = true;
				return async ({ update }) => {
					await update({ reset: false });
					busy = false;
				};
			}}
		>
			<input type="hidden" name="itemId" value={o.itemId} />
			<input type="hidden" name="dueDate" value={o.dueDate} />
			<input type="hidden" name="paid" value={o.paid ? '0' : '1'} />
			<button
				class="btn btn-circle min-h-11 min-w-11 btn-ghost btn-sm {o.paid
					? 'text-success'
					: 'text-base-content/40'}"
				disabled={busy}
				aria-label={o.paid ? `${o.name}: paid. Mark unpaid` : `Mark ${o.name} paid`}
				data-testid="plan-tick"
			>
				{#if busy}
					<span class="loading loading-xs loading-spinner"></span>
				{:else}
					<Icon name={o.paid ? 'circle-check' : 'circle'} size={22} />
				{/if}
			</button>
		</form>
	{/if}
	<a href="/bills/{o.itemId}" class="min-w-0 flex-1">
		<span class="block truncate text-sm font-medium">{lineName(line)}</span>
		<span class="block truncate text-xs text-base-content/60">
			{formatDate(o.dueDate)} ·
			<span
				class={status === 'Paid'
					? 'text-success'
					: status === 'Late'
						? 'text-error'
						: 'text-base-content/60'}
				data-testid="plan-status">{status}{o.paid?.auto ? ' · auto' : ''}</span
			>
		</span>
	</a>
	<span
		class="shrink-0 font-mono text-sm tabular {o.paid ? 'text-base-content/45 line-through' : ''}"
		>{signedMoney((income ? 1 : -1) * (o.paid?.amountCents ?? o.amountCents))}</span
	>
</li>
