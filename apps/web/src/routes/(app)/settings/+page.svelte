<script lang="ts">
	import { enhance } from '$app/forms';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import ThemePicker from '#lib/components/ThemePicker.svelte';
	import InstallHint from '#lib/components/InstallHint.svelte';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data, form }: PageProps = $props();
	let changingPassword = $state(false);

	$effect(() => {
		if (form?.profileSaved) toasts.show('Name updated', 'success');
		if (form?.passwordSaved) {
			toasts.show('Password changed', 'success');
			changingPassword = false;
		}
	});
</script>

<svelte:head><title>Settings · CouplesCash</title></svelte:head>

<AppBar title="Settings" back="/more" />

<main class="flex flex-col gap-4 px-4 pt-2">
	<section class="card border border-base-300/60 bg-base-100 shadow-sm" aria-labelledby="theme">
		<div class="card-body gap-3 p-5">
			<h2 id="theme" class="font-semibold">Appearance</h2>
			<ThemePicker theme={data.theme} />
			<p class="text-xs text-base-content/60">
				System follows your device's light or dark setting.
			</p>
		</div>
	</section>

	<section class="card border border-base-300/60 bg-base-100 shadow-sm" aria-labelledby="profile">
		<form method="POST" action="?/profile" class="card-body gap-3 p-5" use:enhance>
			<h2 id="profile" class="font-semibold">Profile</h2>
			<label class="form-control">
				<span class="label-text mb-1 text-sm font-medium">Your name</span>
				<div class="flex gap-2">
					<input
						name="displayName"
						type="text"
						required
						maxlength="40"
						class="input-bordered input min-h-12 flex-1"
						defaultValue={data.me.user.displayName}
					/>
					<button class="btn min-h-12 rounded-xl btn-secondary">Save</button>
				</div>
			</label>
			{#if form?.profileError}<p class="text-sm text-error" role="alert">
					{form.profileError}
				</p>{/if}
			<p class="text-xs text-base-content/60">Signed in as {data.me.user.email}</p>
		</form>
	</section>

	<section class="card border border-base-300/60 bg-base-100 shadow-sm" aria-labelledby="security">
		<div class="card-body gap-3 p-5">
			<div class="flex items-center justify-between">
				<h2 id="security" class="font-semibold">Password</h2>
				{#if !changingPassword}
					<button
						type="button"
						class="btn min-h-11 btn-ghost btn-sm"
						onclick={() => (changingPassword = true)}>Change</button
					>
				{/if}
			</div>
			{#if changingPassword}
				<form method="POST" action="?/password" class="flex flex-col gap-3" use:enhance>
					<label class="form-control">
						<span class="label-text mb-1 text-sm font-medium">Current password</span>
						<input
							name="currentPassword"
							type="password"
							autocomplete="current-password"
							required
							class="input-bordered input min-h-12 w-full"
						/>
					</label>
					<label class="form-control">
						<span class="label-text mb-1 text-sm font-medium">New password</span>
						<input
							name="newPassword"
							type="password"
							autocomplete="new-password"
							required
							minlength="10"
							class="input-bordered input min-h-12 w-full"
						/>
					</label>
					<label class="form-control">
						<span class="label-text mb-1 text-sm font-medium">Confirm new password</span>
						<input
							name="confirmPassword"
							type="password"
							autocomplete="new-password"
							required
							minlength="10"
							class="input-bordered input min-h-12 w-full"
						/>
					</label>
					{#if form?.passwordError}<p class="text-sm text-error" role="alert">
							{form.passwordError}
						</p>{/if}
					<div class="flex gap-2">
						<button class="btn min-h-12 flex-1 rounded-full btn-primary">Update password</button>
						<button
							type="button"
							class="btn min-h-12 rounded-full btn-ghost"
							onclick={() => (changingPassword = false)}>Cancel</button
						>
					</div>
				</form>
			{:else}
				<p class="text-sm text-base-content/60">
					Changing your password signs out every other device.
				</p>
			{/if}
		</div>
	</section>

	<section class="card border border-base-300/60 bg-base-100 shadow-sm" aria-labelledby="install">
		<div class="card-body gap-3 p-5">
			<h2 id="install" class="flex items-center gap-2 font-semibold">
				<Icon name="smartphone" size={18} /> Install the app
			</h2>
			<InstallHint />
		</div>
	</section>

	<form method="POST" action="?/signout" use:enhance>
		<button class="btn min-h-12 w-full rounded-full btn-outline btn-error">
			<Icon name="log-out" size={18} /> Sign out
		</button>
	</form>

	<p class="pb-2 text-center text-xs text-base-content/40">CouplesCash · made for two</p>
</main>
