<script lang="ts">
	import { enhance } from '$app/forms';
	import type { PageProps } from './$types';

	let { form }: PageProps = $props();
	let submitting = $state(false);
</script>

<svelte:head><title>Sign in · CouplesCash</title></svelte:head>

<form
	method="POST"
	class="card border border-base-300/60 bg-base-100 shadow-sm"
	use:enhance={() => {
		submitting = true;
		return async ({ update }) => {
			await update();
			submitting = false;
		};
	}}
>
	<div class="card-body gap-4">
		<h2 class="card-title text-xl">Welcome back</h2>

		{#if form?.error}
			<div class="alert text-sm alert-error" role="alert">{form.error}</div>
		{/if}

		<label class="form-control w-full">
			<span class="label-text mb-1 text-sm font-medium">Email</span>
			<input
				name="email"
				type="email"
				autocomplete="email"
				inputmode="email"
				required
				defaultValue={form?.email ?? ''}
				class="input-bordered input min-h-12 w-full"
				placeholder="you@example.com"
			/>
		</label>

		<label class="form-control w-full">
			<span class="label-text mb-1 text-sm font-medium">Password</span>
			<input
				name="password"
				type="password"
				autocomplete="current-password"
				required
				class="input-bordered input min-h-12 w-full"
				placeholder="••••••••••"
			/>
		</label>

		<button class="btn mt-2 min-h-12 rounded-full btn-primary" disabled={submitting}>
			{#if submitting}<span class="loading loading-sm loading-spinner"></span>{/if}
			Sign in
		</button>

		<p class="text-center text-sm text-base-content/70">
			New here? <a href="/register" class="link font-medium link-primary">Create an account</a>
		</p>
	</div>
</form>
