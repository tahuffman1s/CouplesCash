<script lang="ts">
	import { page } from '$app/state';
	import EmptyState from '#lib/components/EmptyState.svelte';
</script>

<main class="mx-auto flex min-h-dvh w-full max-w-xl items-center p-4">
	<div class="w-full">
		<EmptyState
			icon="circle-alert"
			title={page.status === 404 ? 'Page not found' : 'Something went wrong'}
			body={page.error?.message ?? 'Please try again.'}
			cta="Back to home"
			href="/"
		/>
	</div>
</main>
