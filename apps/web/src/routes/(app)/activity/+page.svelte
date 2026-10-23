<script lang="ts">
	import type { ActivityEvent, ActivityPage } from '#lib/types.ts';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import NoHousehold from '#lib/components/NoHousehold.svelte';
	import ActivityRow from '#lib/components/ActivityRow.svelte';
	import { apiFetch } from '#lib/utils/client.ts';
	import { dayHeading, dayOf } from '#lib/utils/dates.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data }: PageProps = $props();

	// Reset from the server page on invalidation; appended by "Load more" through the proxy
	let events = $derived<ActivityEvent[]>(data.page?.events ?? []);
	let nextBefore = $derived<string | null>(data.page?.nextBefore ?? null);
	let loading = $state(false);

	const groups = $derived.by(() => {
		const out: { day: string; items: ActivityEvent[] }[] = [];
		for (const e of events) {
			const day = dayOf(e.createdAt);
			const last = out[out.length - 1];
			if (last && last.day === day) last.items.push(e);
			else out.push({ day, items: [e] });
		}
		return out;
	});

	async function loadMore() {
		if (!nextBefore || loading) return;
		loading = true;
		try {
			const page = await apiFetch<ActivityPage>(
				`/activity?before=${encodeURIComponent(nextBefore)}&limit=30`
			);
			const seen = new Set(events.map((e) => e.id));
			events = [...events, ...page.events.filter((e) => !seen.has(e.id))];
			nextBefore = page.nextBefore;
		} catch {
			toasts.show('Could not load more.', 'error');
		} finally {
			loading = false;
		}
	}
</script>

<svelte:head><title>Activity · CouplesCash</title></svelte:head>

<AppBar title="Activity" back="/more" />

<main class="flex flex-col gap-4 px-4 pt-2">
	{#if !data.page}
		<NoHousehold />
	{:else if events.length === 0}
		<EmptyState
			icon="activity"
			title="Nothing yet"
			body="Every change either of you makes – a bill paid, a balance updated, an expense added – shows up here with who did it."
			cta="Add an expense"
			href="/expenses/new"
		/>
	{:else}
		{#each groups as g (g.day)}
			<section aria-labelledby="day-{g.day}">
				<h2
					id="day-{g.day}"
					class="mb-1 px-1 text-sm font-semibold tracking-wide text-base-content/70 uppercase"
				>
					{dayHeading(g.day)}
				</h2>
				<ul
					class="card divide-y divide-base-300/60 border border-base-300/60 bg-base-100 px-4 shadow-sm"
				>
					{#each g.items as ev (ev.id)}
						<ActivityRow event={ev} mine={ev.actorId === data.user?.id} />
					{/each}
				</ul>
			</section>
		{/each}
		{#if nextBefore}
			<button
				type="button"
				class="btn min-h-12 rounded-full border border-base-300 btn-ghost"
				disabled={loading}
				onclick={loadMore}
			>
				{#if loading}<span class="loading loading-sm loading-spinner"></span>{/if}
				Load more
			</button>
		{:else}
			<p class="py-2 text-center text-xs text-base-content/50">That's everything.</p>
		{/if}
	{/if}
</main>
