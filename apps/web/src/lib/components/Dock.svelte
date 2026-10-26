<script lang="ts">
	import { page } from '$app/state';
	import { review } from '#lib/state/review.svelte.ts';
	import Icon from './Icon.svelte';

	/** v6: five tabs, each with one job (PLAN-V6 §4). */
	const items = [
		{
			href: '/week',
			label: 'Week',
			icon: 'calendar-range',
			match: (p: string) => p.startsWith('/week')
		},
		{
			href: '/transactions',
			label: 'Transactions',
			icon: 'list',
			match: (p: string) => p.startsWith('/transactions') || p.startsWith('/expenses')
		},
		{
			href: '/bills',
			label: 'Bills',
			icon: 'receipt',
			match: (p: string) => p.startsWith('/bills')
		},
		{
			href: '/accounts',
			label: 'Money',
			icon: 'wallet',
			match: (p: string) =>
				p.startsWith('/accounts') || p.startsWith('/goals') || p.startsWith('/transfers')
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
				p.startsWith('/imports')
		}
	];
</script>

<nav
	class="dock dock-md border-base-300/60 bg-base-100/95 backdrop-blur print:hidden"
	aria-label="Primary"
>
	{#each items as item (item.href)}
		{@const active = item.match(page.url.pathname)}
		{@const badge = item.href === '/transactions' ? review.count : 0}
		<a
			href={item.href}
			aria-current={active ? 'page' : undefined}
			aria-label={badge ? `${item.label}, ${badge} to review` : undefined}
			class="min-h-11 min-w-11 {active ? 'text-primary' : 'text-base-content/70'}"
		>
			<span class="relative">
				<Icon name={item.icon} size={22} strokeWidth={active ? 2.4 : 2} />
				{#if badge}
					<span
						class="absolute -top-2 -right-3 badge h-4.5 min-w-4.5 border-0 bg-primary px-1 badge-xs text-[0.65rem] font-semibold text-primary-content tabular"
						data-testid="review-badge">{badge > 99 ? '99+' : badge}</span
					>
				{/if}
			</span>
			<span class="dock-label font-medium">{item.label}</span>
		</a>
	{/each}
</nav>
