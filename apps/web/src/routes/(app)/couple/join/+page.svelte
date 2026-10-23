<script lang="ts">
	import { enhance } from '$app/forms';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';

	let { data, form }: PageProps = $props();
	const alreadyLinked = $derived(!!data.household && data.household.members.length >= 2);
</script>

<svelte:head><title>Join household · CouplesCash</title></svelte:head>

<AppBar title="Join household" back="/couple" />

<main class="flex flex-col gap-4 px-4 pt-2">
	{#if alreadyLinked}
		<div class="alert" role="status">
			<Icon name="info" size={20} />
			<span>You're already linked with your partner.</span>
			<a href="/couple" class="btn btn-sm">Open</a>
		</div>
	{:else}
		<form method="POST" class="card border border-base-300/60 bg-base-100 shadow-sm" use:enhance>
			<div class="card-body items-center gap-4 p-6 text-center">
				<span
					class="flex h-16 w-16 items-center justify-center rounded-full bg-primary/10 text-primary"
				>
					<Icon name="heart-handshake" size={30} />
				</span>
				<h2 class="text-xl font-bold">Your partner invited you</h2>
				{#if data.code}
					<p class="text-sm text-base-content/65">
						Join their household to share accounts, bills and the calendar.
					</p>
					<input type="hidden" name="code" value={data.code} />
				{:else}
					<p class="text-sm text-base-content/65">
						This link is missing a valid code. Enter it below instead.
					</p>
					<input
						name="code"
						type="text"
						required
						autocomplete="off"
						autocapitalize="characters"
						class="input-bordered input min-h-14 w-full text-center text-2xl tracking-[0.2em] uppercase tabular"
						placeholder="ABCD-EFGH"
					/>
				{/if}
				{#if form?.error}<p class="text-sm text-error" role="alert">{form.error}</p>{/if}
				<button class="btn min-h-12 w-full rounded-full btn-primary">
					<Icon name="link" size={18} /> Join household
				</button>
				<a href="/couple" class="link text-sm">Not now</a>
			</div>
		</form>
	{/if}
</main>
