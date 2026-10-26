<script lang="ts">
	import Icon from './Icon.svelte';
	import { SETUP_STEPS, firstOpenStep, type SetupStep } from '#lib/utils/setup.ts';

	/**
	 * v7 first run: the six setup steps instead of the plan, each ticked by real data and each
	 * reachable on its own (skip any, come back from Settings).
	 */
	let { done, paydayName }: { done: Record<SetupStep, boolean>; paydayName: string } = $props();

	const open = $derived(firstOpenStep(done));
	const count = $derived(SETUP_STEPS.filter((s) => done[s.key]).length);
</script>

<section
	class="card border border-base-300/60 bg-base-100 shadow-sm"
	aria-labelledby="setup-h"
	data-testid="setup-overview"
>
	<div class="card-body gap-3 p-5">
		<div>
			<h2 id="setup-h" class="text-lg font-semibold">Let's set up your {paydayName}</h2>
			<p class="text-sm text-base-content/65">
				A few minutes once, then this page does the weekly math for you: what's in the bank, the
				bills before next payday, and what's left at the end of the week.
			</p>
		</div>
		<ol class="flex flex-col">
			{#each SETUP_STEPS as s, i (s.key)}
				<li>
					<a
						href="/setup?step={s.key}"
						class="flex min-h-14 items-center gap-3 rounded-xl px-1 py-2 hover:bg-base-200"
						data-testid="setup-step-link"
						data-done={done[s.key] ? 'true' : 'false'}
					>
						<span
							class="flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-sm font-semibold {done[
								s.key
							]
								? 'bg-success text-success-content'
								: 'bg-base-200 text-base-content/70'}"
						>
							{#if done[s.key]}<Icon name="check" size={16} />{:else}{i + 1}{/if}
						</span>
						<span class="min-w-0 flex-1">
							<span class="block font-medium {done[s.key] ? 'text-base-content/60' : ''}"
								>{s.title}</span
							>
							<span class="block text-xs text-base-content/60">{s.hint}</span>
						</span>
						<Icon name="chevron-right" size={18} class="text-base-content/40" />
					</a>
				</li>
			{/each}
		</ol>
		<a
			href="/setup{open ? `?step=${open}` : ''}"
			class="btn min-h-12 rounded-full btn-primary"
			data-testid="setup-start">{count === 0 ? 'Start' : 'Continue'}</a
		>
	</div>
</section>
