<script lang="ts">
	import { page } from '$app/state';
	import Icon from './Icon.svelte';

	const items = [
		{ href: '/', label: 'Home', icon: 'house', match: (p: string) => p === '/' },
		{
			href: '/accounts',
			label: 'Accounts',
			icon: 'wallet',
			match: (p: string) => p.startsWith('/accounts') || p.startsWith('/goals')
		},
		{
			href: '/bills',
			label: 'Bills',
			icon: 'receipt',
			match: (p: string) => p.startsWith('/bills') || p.startsWith('/expenses')
		},
		{
			href: '/calendar',
			label: 'Calendar',
			icon: 'calendar-days',
			match: (p: string) => p.startsWith('/calendar')
		},
		{
			href: '/more',
			label: 'More',
			icon: 'ellipsis',
			match: (p: string) =>
				p.startsWith('/more') ||
				p.startsWith('/settings') ||
				p.startsWith('/insights') ||
				p.startsWith('/couple') ||
				p.startsWith('/activity') ||
				p.startsWith('/todos') ||
				p.startsWith('/payperiod')
		}
	];
</script>

<nav
	class="dock dock-md border-base-300/60 bg-base-100/95 backdrop-blur print:hidden"
	aria-label="Primary"
>
	{#each items as item (item.href)}
		{@const active = item.match(page.url.pathname)}
		<a
			href={item.href}
			aria-current={active ? 'page' : undefined}
			class="min-h-11 min-w-11 {active ? 'text-primary' : 'text-base-content/70'}"
		>
			<Icon name={item.icon} size={22} strokeWidth={active ? 2.4 : 2} />
			<span class="dock-label font-medium">{item.label}</span>
		</a>
	{/each}
</nav>
