<script lang="ts">
	import { enhance } from '$app/forms';
	import type { PageProps } from './$types';
	import Icon from '#lib/components/Icon.svelte';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data, form }: PageProps = $props();
	let submitting = $state(false);
	let passkeyReady = $state(false);
	let passkeyBusy = $state(false);
	let passkeyError = $state('');

	type Options = { challengeId: string; options: unknown };

	async function bff<T>(method: 'GET' | 'POST', body?: unknown): Promise<T> {
		const res = await fetch('/login/passkey', {
			method,
			headers: {
				'X-Requested-With': 'CouplesCash',
				accept: 'application/json',
				...(body ? { 'content-type': 'application/json' } : {})
			},
			body: body ? JSON.stringify(body) : undefined
		});
		const json = (await res.json().catch(() => ({}))) as T & { title?: string };
		if (!res.ok) throw new Error(json.title ?? 'Sign-in failed');
		return json;
	}

	async function signInWithPasskey(autofill = false) {
		if (passkeyBusy && !autofill) return;
		passkeyBusy = !autofill;
		passkeyError = '';
		try {
			const { startAuthentication } = await import('@simplewebauthn/browser');
			const { challengeId, options } = await bff<Options>('GET');
			const credential = await startAuthentication({
				optionsJSON: options as Parameters<typeof startAuthentication>[0]['optionsJSON'],
				useBrowserAutofill: autofill
			});
			await bff('POST', { challengeId, credential });
			toasts.show('Signed in with your passkey', 'success');
			window.location.assign(data.next);
		} catch (e) {
			const name = (e as Error)?.name;
			if (name === 'NotAllowedError' || name === 'AbortError') {
				// cancelled by the user (or superseded by a button click) – stay quiet for autofill
				if (!autofill) passkeyError = 'Passkey sign-in was cancelled.';
			} else if (!autofill) {
				passkeyError = (e as Error).message || 'Passkey sign-in failed.';
			}
		} finally {
			if (!autofill) passkeyBusy = false;
		}
	}

	$effect(() => {
		if (!data.passkeysEnabled) return;
		if (!window.isSecureContext || !('PublicKeyCredential' in window)) return;
		(async () => {
			const { browserSupportsWebAuthn, browserSupportsWebAuthnAutofill } =
				await import('@simplewebauthn/browser');
			if (!browserSupportsWebAuthn()) return;
			passkeyReady = true;
			// Conditional UI: offer saved passkeys in the email field's autofill when available
			if (await browserSupportsWebAuthnAutofill()) void signInWithPasskey(true);
		})();
	});
</script>

<svelte:head><title>Sign in · CouplesCash</title></svelte:head>

<div class="flex flex-col gap-3">
	{#if passkeyReady}
		<button
			type="button"
			class="btn min-h-12 rounded-full border border-base-300 btn-ghost"
			disabled={passkeyBusy}
			onclick={() => signInWithPasskey(false)}
			data-testid="passkey-login"
		>
			{#if passkeyBusy}<span class="loading loading-sm loading-spinner"></span>{:else}<Icon
					name="fingerprint"
					size={20}
				/>{/if}
			Sign in with a passkey
		</button>
		{#if passkeyError}<p class="text-center text-sm text-error" role="alert">{passkeyError}</p>{/if}
		<div class="divider my-0 text-xs text-base-content/50">or use your password</div>
	{/if}

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
					autocomplete="username webauthn"
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
</div>
