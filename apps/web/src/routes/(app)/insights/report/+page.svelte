<script lang="ts">
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import BudgetBar from '#lib/charts/BudgetBar.svelte';
	import { formatMoney } from '#lib/utils/money.ts';
	import { addMonths, formatDate, formatMonth, today } from '#lib/utils/dates.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data }: PageProps = $props();

	const r = $derived(data.report);
	const s = $derived(data.report.summary);
	const first = $derived(`${data.month}-01`);
	const paidPct = $derived(
		s.dueThisMonthCents > 0 ? Math.round((s.paidThisMonthCents / s.dueThisMonthCents) * 100) : 0
	);
	const budgetRows = $derived(
		[...r.budgets]
			.filter((b) => b.budgetCents !== null || b.spentCents > 0)
			.sort((a, b) => b.spentCents - a.spentCents)
			.slice(0, 8)
	);
	const maxSpent = $derived(
		Math.max(1, ...budgetRows.map((b) => Math.max(b.spentCents, b.budgetCents ?? 0)))
	);
	const households = $derived(data.household?.name ?? 'Our household');

	let card = $state<HTMLElement | null>(null);
	let sharing = $state(false);
	const canShare = $derived(typeof navigator !== 'undefined' && 'share' in navigator);

	async function share() {
		if (!card || sharing) return;
		sharing = true;
		try {
			const { toPng } = await import('html-to-image');
			const bg = getComputedStyle(document.documentElement)
				.getPropertyValue('--color-base-100')
				.trim();
			const dataUrl = await toPng(card, {
				pixelRatio: 2,
				backgroundColor: bg || '#ffffff',
				cacheBust: true
			});
			const blob = await (await fetch(dataUrl)).blob();
			const name = `couplescash-${data.month}.png`;
			const file = new File([blob], name, { type: 'image/png' });
			const nav = navigator as Navigator & { canShare?: (d: ShareData) => boolean };
			if (nav.share && (!nav.canShare || nav.canShare({ files: [file] }))) {
				try {
					await nav.share({ files: [file], title: `${households} · ${formatMonth(first)}` });
					return;
				} catch (e) {
					if ((e as Error).name === 'AbortError') return;
				}
			}
			const a = document.createElement('a');
			a.href = dataUrl;
			a.download = name;
			a.click();
			toasts.show('Report image downloaded', 'success');
		} catch {
			toasts.show('Could not render the report image.', 'error');
		} finally {
			sharing = false;
		}
	}
</script>

<svelte:head><title>Month report · CouplesCash</title></svelte:head>

<AppBar title="Month report" back="/insights?m={data.month}">
	{#snippet action()}
		<button
			type="button"
			class="btn btn-circle min-h-11 min-w-11 btn-ghost btn-sm"
			aria-label="Print"
			onclick={() => window.print()}
		>
			<Icon name="printer" size={20} />
		</button>
		<button
			type="button"
			class="btn min-h-11 rounded-full px-4 btn-primary btn-sm"
			disabled={sharing}
			onclick={share}
			data-testid="share-report"
		>
			{#if sharing}<span class="loading loading-xs loading-spinner"></span>{:else}<Icon
					name={canShare ? 'share' : 'download'}
					size={18}
				/>{/if}
			Share
		</button>
	{/snippet}
</AppBar>

<main class="flex flex-col gap-4 px-4 pt-2">
	<div class="flex items-center justify-between print:hidden">
		<a
			href="?m={addMonths(first, -1).slice(0, 7)}"
			class="btn btn-circle min-h-11 min-w-11 btn-ghost"
			aria-label="Previous month"
		>
			<Icon name="chevron-left" size={22} />
		</a>
		<h2 class="font-semibold">{formatMonth(first)}</h2>
		<a
			href="?m={addMonths(first, 1).slice(0, 7)}"
			class="btn btn-circle min-h-11 min-w-11 btn-ghost"
			aria-label="Next month"
		>
			<Icon name="chevron-right" size={22} />
		</a>
	</div>

	<article
		bind:this={card}
		class="card border border-base-300/60 bg-base-100 text-base-content shadow-sm print:border-0 print:shadow-none"
		data-testid="month-report"
		aria-label="Month report for {formatMonth(first)}"
	>
		<div class="card-body gap-5 p-6">
			<!-- Header -->
			<header class="flex items-start justify-between gap-3">
				<div>
					<p class="text-xs font-medium tracking-wide text-base-content/60 uppercase">
						{households}
					</p>
					<h2 class="text-2xl font-bold tracking-tight">{formatMonth(first)}</h2>
				</div>
				<span
					class="flex h-10 w-10 items-center justify-center rounded-xl bg-primary text-primary-content"
					aria-hidden="true"
				>
					<Icon name="heart" size={20} strokeWidth={2.4} />
				</span>
			</header>

			<!-- Net worth -->
			<section aria-labelledby="rep-nw">
				<p id="rep-nw" class="text-xs text-base-content/60">Net worth</p>
				<p class="text-4xl leading-none font-bold tracking-tight">
					{formatMoney(s.netWorthCents, { whole: true })}
				</p>
				{#if r.netWorthDeltaCents !== null}
					<p class="mt-1 text-sm {r.netWorthDeltaCents >= 0 ? 'text-success' : 'text-error'}">
						{formatMoney(r.netWorthDeltaCents, { signed: true, whole: true })} vs {formatMonth(
							addMonths(first, -1)
						)}
					</p>
				{:else}
					<p class="mt-1 text-sm text-base-content/50">First month on record</p>
				{/if}
			</section>

			<!-- Paid vs due -->
			<section aria-labelledby="rep-paid" class="grid gap-2">
				<div class="flex items-baseline justify-between text-sm">
					<h3 id="rep-paid" class="font-semibold">Bills paid</h3>
					<span class="tabular">
						{formatMoney(s.paidThisMonthCents)}
						<span class="text-base-content/50"> of {formatMoney(s.dueThisMonthCents)}</span>
					</span>
				</div>
				<progress
					class="progress h-2.5 w-full progress-secondary"
					value={paidPct}
					max="100"
					aria-label="{paidPct}% of bills paid"
				></progress>
				<p class="text-xs text-base-content/60">
					{paidPct}% · {r.paidCount} paid, {r.unpaidCount} still open
					{#if r.expensesTotalCents > 0}
						· {formatMoney(r.expensesTotalCents)} in one-off expenses
					{/if}
				</p>
			</section>

			<!-- By category vs budget -->
			{#if budgetRows.length}
				<section aria-labelledby="rep-cat" class="grid gap-2.5">
					<h3 id="rep-cat" class="text-sm font-semibold">Spending by category</h3>
					<ul class="grid gap-2.5" aria-label="Spent vs budget by category">
						{#each budgetRows as b (b.categoryId)}
							{@const over = b.budgetCents !== null && b.spentCents > b.budgetCents}
							<li class="grid gap-1 text-xs">
								<div class="flex items-center justify-between gap-2">
									<span class="flex items-center gap-1.5 font-medium">
										<span class="h-2 w-2 rounded-full" style:background={b.color} aria-hidden="true"
										></span>
										{b.name}
									</span>
									<span
										class="tabular {over ? 'font-semibold text-error' : 'text-base-content/70'}"
									>
										{formatMoney(b.spentCents, { whole: true })}{#if b.budgetCents !== null}<span
												class="text-base-content/50"
												>{` / ${formatMoney(b.budgetCents, { whole: true })}`}</span
											>{/if}
									</span>
								</div>
								{#if b.budgetCents !== null}
									<BudgetBar
										spent={b.spentCents}
										budget={b.budgetCents}
										color={b.color}
										label={b.name}
									/>
								{:else}
									<BudgetBar
										spent={b.spentCents}
										budget={maxSpent}
										color={b.color}
										label={b.name}
									/>
								{/if}
							</li>
						{/each}
					</ul>
				</section>
			{/if}

			<!-- Top expenses -->
			{#if r.topExpenses.length}
				<section aria-labelledby="rep-top">
					<h3 id="rep-top" class="mb-1 text-sm font-semibold">Biggest expenses</h3>
					<ol class="divide-y divide-base-300/60 text-sm">
						{#each r.topExpenses.slice(0, 5) as e, i (e.id)}
							<li class="flex items-center gap-3 py-1.5">
								<span class="w-4 text-xs text-base-content/50 tabular">{i + 1}</span>
								<span class="flex-1 truncate">{e.name}</span>
								<span class="text-xs text-base-content/50">{formatDate(e.spentOn)}</span>
								<span class="w-20 text-right font-medium tabular">{formatMoney(e.amountCents)}</span
								>
							</li>
						{/each}
					</ol>
				</section>
			{/if}

			<!-- Autopay -->
			{#if r.autoPaid.length}
				<section aria-labelledby="rep-auto">
					<h3 id="rep-auto" class="mb-1 flex items-center gap-1.5 text-sm font-semibold">
						<Icon name="bot" size={14} class="text-secondary" /> Auto-paid
					</h3>
					<ul class="divide-y divide-base-300/60 text-sm">
						{#each r.autoPaid as o (o.itemId + o.dueDate)}
							<li class="flex items-center gap-3 py-1.5">
								<span class="flex-1 truncate">{o.name}</span>
								<span class="text-xs text-base-content/50">{formatDate(o.dueDate)}</span>
								<span class="w-20 text-right font-medium tabular">
									{formatMoney(o.paid?.amountCents ?? o.amountCents)}
								</span>
							</li>
						{/each}
					</ul>
				</section>
			{/if}

			<!-- Counts -->
			<section class="grid grid-cols-3 gap-2 text-center text-xs" aria-label="Counts">
				<div class="rounded-xl bg-base-200 px-2 py-2">
					<p class="text-base-content/60">Accounts</p>
					<p class="text-base font-semibold tabular">{s.accounts.length}</p>
				</div>
				<div class="rounded-xl bg-base-200 px-2 py-2">
					<p class="text-base-content/60">Recurring</p>
					<p class="text-base font-semibold tabular">{s.byKind.reduce((a, k) => a + k.count, 0)}</p>
				</div>
				<div class="rounded-xl bg-base-200 px-2 py-2">
					<p class="text-base-content/60">Expenses</p>
					<p class="text-base font-semibold tabular">
						{formatMoney(r.expensesTotalCents, { whole: true })}
					</p>
				</div>
			</section>

			<footer class="border-t border-base-300/60 pt-3 text-center text-xs text-base-content/50">
				Generated with CouplesCash · {formatDate(today())}
			</footer>
		</div>
	</article>
</main>
