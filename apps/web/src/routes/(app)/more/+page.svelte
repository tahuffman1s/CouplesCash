<script lang="ts">
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';

	let { data }: PageProps = $props();
	/** v6: four rows (PLAN-V6 §4). Everything else lives in a tab or in Settings. */
	const links = [
		{
			href: '/insights',
			icon: 'chart-pie',
			label: 'Insights',
			body: 'Where the money goes each month'
		},
		{
			href: '/todos',
			icon: 'list-checks',
			label: 'To pay',
			body: "A shared checklist for things that aren't bills"
		},
		{ href: '/activity', icon: 'activity', label: 'Activity', body: 'Who changed what, and when' },
		{
			href: '/settings',
			icon: 'settings',
			label: 'Settings',
			body: 'You, your household, your banks'
		}
	];
</script>

<svelte:head><title>More · CouplesCash</title></svelte:head>

<AppBar title="More" />

<main class="flex flex-col gap-4 px-4 pt-2">
	<div class="flex items-center gap-3 px-1">
		<div class="avatar avatar-placeholder">
			<div class="w-12 rounded-full bg-primary text-primary-content">
				<span class="text-lg font-semibold"
					>{(data.user?.name ?? '?').slice(0, 1).toUpperCase()}</span
				>
			</div>
		</div>
		<div class="min-w-0">
			<p class="truncate font-semibold">{data.user?.name}</p>
			<p class="truncate text-xs text-base-content/60">
				{data.household ? data.household.name : data.user?.email}
			</p>
		</div>
	</div>
	<ul class="card divide-y divide-base-300/60 border border-base-300/60 bg-base-100 shadow-sm">
		{#each links as l (l.href)}
			<li>
				<a href={l.href} class="flex min-h-16 items-center gap-3 px-4 py-3">
					<span
						class="flex h-10 w-10 items-center justify-center rounded-xl bg-base-200 text-primary"
					>
						<Icon name={l.icon} size={20} />
					</span>
					<span class="flex-1">
						<span class="block font-medium">{l.label}</span>
						<span class="block text-xs text-base-content/60">{l.body}</span>
					</span>
					<Icon name="chevron-right" size={18} class="text-base-content/40" />
				</a>
			</li>
		{/each}
	</ul>
</main>
