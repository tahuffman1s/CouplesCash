<script lang="ts">
	import type { InboxItem } from '#lib/types.ts';
	import { formatDate } from '#lib/utils/dates.ts';
	import { reviewAction } from '#lib/utils/transactions.ts';
	import Amount from './Amount.svelte';
	import Icon from './Icon.svelte';

	/**
	 * v6 one-tap To-review row (PLAN-V6 §5): one big button that says the suggestion ("It's Rent",
	 * "Groceries", "Transfer", or "Choose…"), a secondary "Something else" (the transaction sheet).
	 * Swipe right = the big button, swipe left = Ignore (the page shows an Undo toast).
	 */
	let {
		item,
		busy = false,
		onprimary,
		onelse,
		onignore
	}: {
		item: InboxItem;
		busy?: boolean;
		onprimary: (item: InboxItem) => void;
		onelse: (item: InboxItem) => void;
		onignore: (item: InboxItem) => void;
	} = $props();

	const action = $derived(reviewAction(item));
	const title = $derived(item.payee || item.description);
	const detail = $derived(item.payee && item.description !== item.payee ? item.description : null);
	const sub = $derived(
		[
			item.accountName ?? 'No account',
			formatDate(item.postedOn),
			item.source === 'csv' ? 'CSV' : null
		]
			.filter(Boolean)
			.join(' · ')
	);
	const ICON = {
		occurrence: 'receipt',
		expense: 'tag',
		transfer: 'arrow-left-right',
		choose: 'list'
	};

	// --- swipe (pointer events; vertical scrolling wins when the finger moves up/down first)
	const THRESHOLD = 88;
	let dx = $state(0);
	let dragging = $state(false);
	let startX = 0;
	let startY = 0;
	let tracking = false;
	let decided: 'h' | 'v' | null = null;
	let swallowClick = false;

	function down(e: PointerEvent) {
		if (busy || e.button > 0) return;
		tracking = true;
		decided = null;
		startX = e.clientX;
		startY = e.clientY;
	}
	function move(e: PointerEvent) {
		if (!tracking) return;
		const x = e.clientX - startX;
		const y = e.clientY - startY;
		if (!decided && (Math.abs(x) > 8 || Math.abs(y) > 8))
			decided = Math.abs(x) > Math.abs(y) ? 'h' : 'v';
		if (decided !== 'h') return;
		if (!dragging) (e.currentTarget as HTMLElement).setPointerCapture?.(e.pointerId);
		dragging = true;
		dx = Math.max(-140, Math.min(140, x));
	}
	function up() {
		if (!tracking) return;
		tracking = false;
		if (dragging) {
			swallowClick = true;
			setTimeout(() => (swallowClick = false), 0);
			if (dx >= THRESHOLD) onprimary(item);
			else if (dx <= -THRESHOLD) onignore(item);
		}
		dragging = false;
		dx = 0;
	}
	function guard(e: MouseEvent) {
		if (swallowClick) {
			e.preventDefault();
			e.stopPropagation();
		}
	}
</script>

<li
	class="relative overflow-hidden rounded-2xl"
	data-testid="review-row"
	data-id={item.id}
	data-status={item.status}
>
	<!-- what a swipe will do -->
	<div
		class="absolute inset-0 flex items-center justify-between px-5 text-sm font-semibold"
		aria-hidden="true"
	>
		<span class="flex items-center gap-1.5 text-success {dx > 16 ? 'opacity-100' : 'opacity-0'}">
			<Icon name="check" size={18} />
			{action.label}
		</span>
		<span
			class="flex items-center gap-1.5 text-base-content/70 {dx < -16
				? 'opacity-100'
				: 'opacity-0'}"
		>
			Ignore <Icon name="circle-minus" size={18} />
		</span>
	</div>
	<div
		class="relative flex touch-pan-y flex-col gap-2.5 border border-base-300/60 bg-base-100 p-3.5 shadow-sm select-none {dragging
			? ''
			: 'transition-transform duration-200 motion-reduce:transition-none'} rounded-2xl"
		style:transform="translateX({dx}px)"
		data-testid="review-swipe"
		onpointerdown={down}
		onpointermove={move}
		onpointerup={up}
		onpointercancel={up}
		onclickcapture={guard}
		role="group"
		aria-label="{title} {item.amountCents < 0 ? 'out' : 'in'}"
	>
		<div class="flex items-start gap-3">
			<div class="min-w-0 flex-1">
				<p class="flex items-center gap-1.5">
					<span class="truncate font-medium">{title}</span>
					{#if item.pending}
						<span
							class="badge shrink-0 border-0 bg-warning/20 px-1.5 badge-xs text-[0.65rem] font-medium text-pending"
							>Pending</span
						>
					{/if}
				</p>
				{#if detail}<p class="truncate text-xs text-base-content/60">{detail}</p>{/if}
				<p class="truncate text-xs text-base-content/60" data-testid="review-account">{sub}</p>
			</div>
			<Amount cents={item.amountCents} class="shrink-0 font-semibold" testid="review-amount" />
		</div>
		<div class="flex items-center gap-2">
			<button
				type="button"
				class="btn min-h-12 flex-1 justify-start gap-2 rounded-xl px-4 {action.kind === 'choose'
					? 'border border-base-300 btn-ghost'
					: 'btn-soft btn-primary'}"
				disabled={busy}
				data-testid="review-primary"
				onclick={() => onprimary(item)}
			>
				{#if busy}<span class="loading loading-sm loading-spinner"></span>{:else}<Icon
						name={ICON[action.kind]}
						size={18}
					/>{/if}
				<span class="truncate">{action.label}</span>
			</button>
			<button
				type="button"
				class="btn min-h-12 shrink-0 rounded-xl border border-base-300 btn-ghost px-3 text-sm font-medium"
				disabled={busy}
				data-testid="review-else"
				onclick={() => onelse(item)}
			>
				Something else
			</button>
		</div>
	</div>
</li>
