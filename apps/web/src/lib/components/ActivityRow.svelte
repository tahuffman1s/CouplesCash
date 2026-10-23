<script lang="ts">
	import type { ActivityEvent } from '#lib/types.ts';
	import { relativeTime } from '#lib/utils/dates.ts';
	import { activityActor, isSystemActivity } from '#lib/utils/labels.ts';
	import Icon from './Icon.svelte';

	let { event, mine = false }: { event: ActivityEvent; mine?: boolean } = $props();

	const system = $derived(isSystemActivity(event.kind) || !event.actorId);
	const actor = $derived(activityActor(event.actorName, event.kind));
	const href = $derived.by(() => {
		const id = event.entityId;
		switch (event.entityType) {
			case 'account':
				return id ? `/accounts/${id}` : '/accounts';
			case 'recurring':
			case 'occurrence':
				return id ? `/bills/${id}` : '/calendar';
			case 'expense':
				return id ? `/expenses/${id}` : '/bills';
			case 'goal':
				return id ? `/goals/${id}` : '/goals';
			case 'todo':
				return '/todos';
			case 'budget':
				return '/insights';
			case 'couple':
				return '/couple';
			default:
				return null;
		}
	});
</script>

<li class="flex items-start gap-3 py-2.5">
	{#if system}
		<span
			class="flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-secondary/15 text-secondary"
			aria-hidden="true"
		>
			<Icon name="bot" size={18} />
		</span>
	{:else}
		<span
			class="flex h-9 w-9 shrink-0 items-center justify-center rounded-full text-sm font-semibold {mine
				? 'bg-primary text-primary-content'
				: 'bg-secondary text-secondary-content'}"
			aria-hidden="true"
		>
			{actor.slice(0, 1).toUpperCase()}
		</span>
	{/if}
	<div class="min-w-0 flex-1">
		<p class="text-sm leading-snug">
			<span class="font-semibold">{actor}</span>
			{#if href}
				<a {href} class="text-base-content/90">{event.summary}</a>
			{:else}
				<span class="text-base-content/90">{event.summary}</span>
			{/if}
		</p>
		<p class="mt-0.5 text-xs text-base-content/50">
			<time datetime={event.createdAt}>{relativeTime(event.createdAt)}</time>
			{#if system}<span aria-hidden="true"> · </span><span>Autopay</span>{/if}
		</p>
	</div>
</li>
