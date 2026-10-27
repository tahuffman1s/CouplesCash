<script lang="ts">
	import { untrack } from 'svelte';
	import type { PageProps } from './$types';
	import AccountsStrip from '#lib/components/AccountsStrip.svelte';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import NoHousehold from '#lib/components/NoHousehold.svelte';
	import PaydayChecklist from '#lib/components/PaydayChecklist.svelte';
	import PlanReceipt from '#lib/components/PlanReceipt.svelte';
	import QuickAddSheet, { quickAddForm } from '#lib/components/QuickAddSheet.svelte';
	import SetupOverview from '#lib/components/SetupOverview.svelte';
	import { addDays } from '#lib/utils/dates.ts';
	import { planRangeLabel, weekdayName } from '#lib/utils/plan.ts';
	import { firstOpenStep, stepsDone } from '#lib/utils/setup.ts';
	import { review } from '#lib/state/review.svelte.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data, form }: PageProps = $props();

	const plan = $derived(data.plan);
	const paydayName = $derived(weekdayName(data.household?.paydayWeekday ?? 5));
	const funSet = $derived(data.items.some((i) => i.kind === 'transfer'));
	const done = $derived(stepsDone(plan?.setup, { funSet }));
	const openStep = $derived(firstOpenStep(done));
	const toReview = $derived(plan ? Math.max(plan.toReviewCount, 0) : review.count);

	let quickOpen = $state(false);
	const quick = $derived(quickAddForm(form));
	$effect(() => {
		if (data.joined) untrack(() => toasts.show('You are in! Welcome to the household.', 'success'));
	});
	$effect(() => {
		const f = form as Record<string, unknown> | null;
		if (typeof f?.planError === 'string') toasts.show(f.planError, 'error');
		if (f?.swept === true) toasts.show('Moved to savings ✓', 'success');
	});
</script>

<svelte:head><title>This week · CouplesCash</title></svelte:head>

<AppBar title="This week">
	{#snippet action()}
		{#if data.household && plan?.billsAccount}
			<button
				type="button"
				class="btn btn-circle min-h-11 min-w-11 btn-primary btn-sm"
				aria-label="Add something"
				data-testid="quick-add"
				onclick={() => (quickOpen = true)}
			>
				<Icon name="plus" size={22} strokeWidth={2.5} />
			</button>
		{/if}
	{/snippet}
</AppBar>

<main class="flex flex-col gap-3 px-4 pt-1 pb-4">
	{#if !data.household}
		<NoHousehold />
	{:else if !plan}
		<p class="py-6 text-center text-sm text-base-content/60">Could not load this week.</p>
	{:else if !plan.billsAccount}
		<SetupOverview {done} {paydayName} />
	{:else}
		<!-- the pay week -->
		<div class="flex items-center justify-between gap-1">
			<a
				href="?date={addDays(plan.start, -7)}"
				class="btn btn-circle min-h-11 min-w-11 btn-ghost"
				aria-label="Previous week"
				data-sveltekit-noscroll
			>
				<Icon name="chevron-left" size={22} />
			</a>
			<div class="min-w-0 text-center">
				<h2 class="text-base font-semibold whitespace-nowrap tabular" data-testid="week-label">
					{planRangeLabel(plan.start, plan.end)}
				</h2>
				{#if !plan.isCurrent}
					<a href="/week" class="text-xs text-primary" data-testid="week-today">Back to this week</a
					>
				{/if}
			</div>
			<a
				href="?date={plan.nextPayday}"
				class="btn btn-circle min-h-11 min-w-11 btn-ghost"
				aria-label="Next week"
				data-sveltekit-noscroll
			>
				<Icon name="chevron-right" size={22} />
			</a>
		</div>

		{#if plan.isCurrent && plan.checklist.length}
			<PaydayChecklist items={plan.checklist} isPayday={plan.isPayday} />
		{/if}

		<AccountsStrip accounts={plan.accounts} />

		<PlanReceipt {plan} />

		{#if toReview > 0}
			<a
				href="/transactions"
				class="flex min-h-12 items-center gap-2 rounded-2xl bg-base-200/70 px-4 text-sm font-medium"
				data-testid="review-line"
			>
				<Icon name="inbox" size={18} class="text-primary" />
				<span class="flex-1">{toReview} to review</span>
				<Icon name="arrow-right" size={16} class="text-base-content/50" />
			</a>
		{/if}

		{#if openStep && openStep !== 'banks' && openStep !== 'roles'}
			<a
				href="/setup?step={openStep}"
				class="flex min-h-11 items-center gap-2 px-1 text-xs text-base-content/60"
				data-testid="finish-setup"
			>
				<Icon name="list-checks" size={14} />
				<span class="flex-1">Finish setting up</span>
				<Icon name="chevron-right" size={14} />
			</a>
		{/if}
	{/if}
</main>

{#if data.household}
	<QuickAddSheet
		bind:open={quickOpen}
		categories={data.categories}
		accounts={data.accounts}
		defaultAccountId={plan?.billsAccount?.id ?? data.household.defaultAccountId}
		form={quick}
	/>
{/if}
