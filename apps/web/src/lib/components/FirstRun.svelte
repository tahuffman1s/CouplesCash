<script lang="ts">
	import Icon from './Icon.svelte';

	/**
	 * v6 first run (PLAN-V6 §4): three steps instead of a screen of zeros. Each ticks itself off from
	 * real data; the parent hides the card when all three are done.
	 */
	let {
		hasAccounts,
		hasBills,
		hasPartner,
		onaddbill
	}: {
		hasAccounts: boolean;
		hasBills: boolean;
		hasPartner: boolean;
		/** opens the quick-add sheet for step 2 */
		onaddbill?: () => void;
	} = $props();

	const done = $derived([hasAccounts, hasBills, hasPartner].filter(Boolean).length);
</script>

<section
	class="card border border-base-300/60 bg-base-100 shadow-sm"
	aria-labelledby="first-run"
	data-testid="first-run"
>
	<div class="card-body gap-3 p-5">
		<div>
			<h2 id="first-run" class="text-lg font-semibold">
				{done === 0 ? "Let's get you set up" : 'Almost set up'}
			</h2>
			<p class="text-sm text-base-content/65">
				{#if done === 0}Three steps and this page shows what's left to spend each week.{/if}
				<span class="font-medium text-base-content/80" data-testid="first-run-progress"
					>{done} of 3 done</span
				>
			</p>
		</div>
		<ol class="flex flex-col gap-1">
			{#snippet step(n: number, ok: boolean, title: string, body: string, testid: string)}
				<span
					class="flex shrink-0 items-center justify-center rounded-full text-sm font-semibold {ok
						? 'h-6 w-6 bg-success text-success-content'
						: 'h-9 w-9 bg-base-200 text-base-content/70'}"
					aria-hidden="true"
				>
					{#if ok}<Icon name="check" size={14} strokeWidth={3} />{:else}{n}{/if}
				</span>
				<span class="min-w-0 flex-1">
					<span class="block {ok ? 'text-sm text-base-content/55 line-through' : 'font-medium'}"
						>{title}</span
					>
					{#if !ok}<span class="block text-xs text-base-content/60">{body}</span>{/if}
				</span>
				<span class="sr-only" data-testid="{testid}-state">{ok ? 'done' : 'to do'}</span>
				{#if !ok}<Icon name="chevron-right" size={18} class="text-base-content/40" />{/if}
			{/snippet}
			<li>
				<a
					href={hasAccounts ? '/accounts' : '/accounts/new'}
					class="flex min-h-11 items-center gap-3 rounded-xl px-1 hover:bg-base-200"
					data-testid="step-accounts"
					data-done={hasAccounts}
				>
					{@render step(
						1,
						hasAccounts,
						'Add your accounts or link a bank',
						'Checking, savings, cards. A linked bank keeps balances up to date.',
						'step-accounts'
					)}
				</a>
			</li>
			<li>
				{#if onaddbill && !hasBills}
					<button
						type="button"
						class="flex min-h-11 w-full items-center gap-3 rounded-xl px-1 text-left hover:bg-base-200"
						data-testid="step-bills"
						data-done={hasBills}
						onclick={onaddbill}
					>
						{@render step(
							2,
							hasBills,
							'Add your bills',
							'Type "rent 1850 1st" and you are done.',
							'step-bills'
						)}
					</button>
				{:else}
					<a
						href="/bills"
						class="flex min-h-11 items-center gap-3 rounded-xl px-1 hover:bg-base-200"
						data-testid="step-bills"
						data-done={hasBills}
					>
						{@render step(
							2,
							hasBills,
							'Add your bills',
							'Type "rent 1850 1st" and you are done.',
							'step-bills'
						)}
					</a>
				{/if}
			</li>
			<li>
				<a
					href="/couple"
					class="flex min-h-11 items-center gap-3 rounded-xl px-1 hover:bg-base-200"
					data-testid="step-partner"
					data-done={hasPartner}
				>
					{@render step(
						3,
						hasPartner,
						'Invite your partner',
						'Share a Couple Code so you both see the same numbers.',
						'step-partner'
					)}
				</a>
			</li>
		</ol>
	</div>
</section>
