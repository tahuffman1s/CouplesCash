<script lang="ts">
	import { enhance } from '$app/forms';
	import type { PageProps } from './$types';

	let { form }: PageProps = $props();
	let submitting = $state(false);
	let password = $state('');
	const strength = $derived(
		password.length === 0 ? 0 : password.length < 10 ? 1 : password.length < 14 ? 2 : 3
	);
</script>

<svelte:head><title>Create account · CouplesCash</title></svelte:head>

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
		<h2 class="card-title text-xl">Create your account</h2>

		{#if form?.error}
			<div class="alert text-sm alert-error" role="alert">{form.error}</div>
		{/if}

		<label class="form-control w-full">
			<span class="label-text mb-1 text-sm font-medium">Your name</span>
			<input
				name="displayName"
				type="text"
				autocomplete="given-name"
				required
				maxlength="40"
				defaultValue={form?.displayName ?? ''}
				class="input-bordered input min-h-12 w-full"
				placeholder="Alex"
			/>
		</label>

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

		<div class="form-control w-full">
			<label for="password" class="label-text mb-1 block text-sm font-medium">Password</label>
			<input
				id="password"
				name="password"
				type="password"
				autocomplete="new-password"
				required
				minlength="10"
				bind:value={password}
				class="input-bordered input min-h-12 w-full"
				placeholder="At least 10 characters"
				aria-describedby="pw-hint"
			/>
			<span id="pw-hint" class="mt-1.5 flex items-center gap-1.5" aria-live="polite">
				{#each [1, 2, 3] as level (level)}
					<span
						class="h-1 flex-1 rounded-full {strength >= level
							? level === 1
								? 'bg-warning'
								: 'bg-success'
							: 'bg-base-300'}"
					></span>
				{/each}
				<span class="w-20 text-right text-xs text-base-content/60">
					{strength === 0 ? '' : strength === 1 ? 'Too short' : strength === 2 ? 'Good' : 'Strong'}
				</span>
			</span>
		</div>

		<button class="btn mt-2 min-h-12 rounded-full btn-primary" disabled={submitting}>
			{#if submitting}<span class="loading loading-sm loading-spinner"></span>{/if}
			Create account
		</button>

		<p class="text-center text-sm text-base-content/70">
			Already have one? <a href="/login" class="link font-medium link-primary">Sign in</a>
		</p>
	</div>
</form>
