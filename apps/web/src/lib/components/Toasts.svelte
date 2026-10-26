<script lang="ts">
	import { toasts } from '#lib/state/toasts.svelte.ts';
</script>

{#if toasts.list.length}
	<div
		class="toast toast-center toast-bottom z-50 mb-[calc(4.5rem+env(safe-area-inset-bottom))] w-full max-w-sm px-4 print:hidden"
		role="status"
		aria-live="polite"
	>
		{#each toasts.list as t (t.id)}
			<div
				class="alert shadow-lg {t.tone === 'success'
					? 'alert-success'
					: t.tone === 'error'
						? 'alert-error'
						: ''}"
				data-testid="toast"
			>
				<span class="text-sm">{t.text}</span>
				{#if t.action}
					<button
						type="button"
						class="btn min-h-9 rounded-full border-current/30 btn-outline btn-sm"
						onclick={() => {
							t.action?.run();
							toasts.dismiss(t.id);
						}}>{t.action.label}</button
					>
				{/if}
				<button
					type="button"
					class="btn btn-ghost btn-xs"
					aria-label="Dismiss"
					onclick={() => toasts.dismiss(t.id)}>✕</button
				>
			</div>
		{/each}
	</div>
{/if}
