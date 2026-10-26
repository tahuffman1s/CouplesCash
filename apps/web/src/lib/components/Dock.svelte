<script lang="ts">
	import { page } from '$app/state';
	import { review } from '#lib/state/review.svelte.ts';
	import Icon from './Icon.svelte';

	/** v7: three tabs (PLAN-V7 "Navigation"); Settings is the gear in the app bar. */
	const items = [
		{
			href: '/week',
			label: 'This week',
			icon: 'calendar-range',
			match: (p: string) => p.startsWith('/week') || p.startsWith('/setup')
		},
		{
			href: '/transactions',
			label: 'Transactions',
			icon: 'list',
			match: (p: string) => p.startsWith('/transactions') || p.startsWith('/accounts')
		},
		{
			href: '/bills',
			label: 'Bills',
			icon: 'receipt',
			match: (p: string) => p.startsWith('/bills')
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
