<script lang="ts">
	import { toasts } from '#lib/state/toasts.svelte.ts';
</script>

{#if toasts.list.length}
	<div
		class="toast toast-center toast-top z-50 mt-[env(safe-area-inset-top)] w-full max-w-sm px-4"
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
			>
				<span class="text-sm">{t.text}</span>
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
