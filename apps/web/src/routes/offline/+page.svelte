<script lang="ts">
	import Icon from '#lib/components/Icon.svelte';
</script>

<svelte:head><title>Offline · CouplesCash</title></svelte:head>

<main
	class="mx-auto flex min-h-dvh w-full max-w-md flex-col items-center justify-center gap-4 px-6 text-center"
>
	<span
		class="flex h-20 w-20 items-center justify-center rounded-full bg-base-300/60 text-base-content/70"
	>
		<Icon name="wifi-off" size={36} />
	</span>
	<h1 class="text-2xl font-bold">You're offline</h1>
	<p class="text-base-content/65">
		CouplesCash needs a connection to show your latest balances and bills. We'll be right here when
		you're back online.
	</p>
	<button
		type="button"
		class="btn min-h-12 rounded-full px-8 btn-primary"
		onclick={() => location.reload()}
	>
		Try again
	</button>
	<a href="/week" class="link text-sm link-primary">Go to home</a>
</main>
