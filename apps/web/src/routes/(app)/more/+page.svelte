<script lang="ts">
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';

	let { data }: PageProps = $props();
	const links = $derived([
		{
			href: '/inbox',
			icon: 'inbox',
			label: 'Inbox',
			body: 'Review transactions from your bank or a CSV',
			badge: data.inboxPending > 0 ? data.inboxPending : 0
		},
		{
			href: '/week',
			icon: 'calendar-range',
			label: 'This week',
			body: "What's left at the end of the week, planned one-offs, weekly budgets"
		},
		{
			href: '/insights',
			icon: 'chart-pie',
			label: 'Insights',
			body: 'Where the money goes each month'
		},
		{
			href: '/activity',
			icon: 'activity',
			label: 'Activity',
			body: 'Who changed what, and when'
		},
		{
			href: '/todos',
			icon: 'list-checks',
			label: 'To pay',
			body: "Shared checklist for things that aren't bills"
		},
		{
			href: '/payperiod',
			icon: 'calendar-clock',
			label: 'Pay period',
			body: "What's due before the next payday"
		},
		{
			href: '/couple',
			icon: 'heart-handshake',
			label: 'Couple',
			body: data.hasHousehold ? 'Your household and partner' : 'Link with your partner'
		},
		{
			href: '/settings/banks',
			icon: 'landmark',
			label: 'Linked banks',
			body: 'Pull balances and transactions with SimpleFIN'
		},
		{
			href: '/imports/csv',
			icon: 'file-spreadsheet',
			label: 'Import CSV',
			body: 'Upload a statement export from your bank'
		},
		{
			href: '/settings/household',
			icon: 'sliders-horizontal',
			label: 'Household settings',
			body: 'Currency, week start, pay period, time zone'
		},
		{
			href: '/settings',
			icon: 'settings',
			label: 'Settings',
			body: 'Theme, profile, notifications, passkeys, install'
		}
	]);
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
			<p class="truncate text-xs text-base-content/60">{data.user?.email}</p>
		</div>
	</div>
	<ul class="card divide-y divide-base-300/60 border border-base-300/60 bg-base-100 shadow-sm">
		{#each links as l (l.href)}
			<li>
				<a href={l.href} class="flex min-h-14 items-center gap-3 px-4 py-3">
					<span
						class="flex h-10 w-10 items-center justify-center rounded-xl bg-base-200 text-primary"
					>
						<Icon name={l.icon} size={20} />
					</span>
					<span class="flex-1">
						<span class="block font-medium">{l.label}</span>
						<span class="block text-xs text-base-content/60">{l.body}</span>
					</span>
					{#if 'badge' in l && l.badge}
						<span class="badge badge-sm tabular badge-primary" data-testid="inbox-badge"
							>{l.badge}</span
						>
					{/if}
					<Icon name="chevron-right" size={18} class="text-base-content/40" />
				</a>
			</li>
		{/each}
	</ul>
</main>
