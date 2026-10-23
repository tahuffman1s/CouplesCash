<script lang="ts">
	import { enhance } from '$app/forms';
	import { invalidateAll } from '$app/navigation';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import { apiFetch } from '#lib/utils/client.ts';
	import {
		detectPushSupport,
		subscriptionBody,
		urlBase64ToUint8Array,
		type PushSupport
	} from '#lib/utils/push.ts';
	import { formatTimestamp } from '#lib/utils/dates.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data, form }: PageProps = $props();

	const prefs = $derived(form?.prefs ?? data.prefs);
	let support = $state<PushSupport | 'checking'>('checking');
	let permission = $state<NotificationPermission | 'unknown'>('unknown');
	let subscribed = $state(false);
	let currentEndpoint = $state('');
	let working = $state(false);

	const DAYS = [0, 1, 2, 3, 7];
	const dayLabel = (d: number) =>
		d === 0 ? 'Same day' : d === 1 ? '1 day before' : `${d} days before`;

	$effect(() => {
		support = detectPushSupport();
		if (support !== 'supported') return;
		permission = Notification.permission;
		void navigator.serviceWorker.ready.then(async (reg) => {
			const sub = await reg.pushManager.getSubscription();
			subscribed = !!sub;
			currentEndpoint = sub?.endpoint ?? '';
		});
	});

	async function enable() {
		if (!data.vapidPublicKey || working) return;
		working = true;
		try {
			const result = await Notification.requestPermission();
			permission = result;
			if (result !== 'granted') {
				toasts.show(
					'Notifications are blocked for this site. Allow them in your browser settings.',
					'error'
				);
				return;
			}
			const reg = await navigator.serviceWorker.ready;
			const sub =
				(await reg.pushManager.getSubscription()) ??
				(await reg.pushManager.subscribe({
					userVisibleOnly: true,
					applicationServerKey: urlBase64ToUint8Array(data.vapidPublicKey) as BufferSource
				}));
			await apiFetch('/push/subscriptions', {
				method: 'POST',
				body: JSON.stringify(subscriptionBody(sub))
			});
			subscribed = true;
			currentEndpoint = sub.endpoint;
			toasts.show('This device will get reminders', 'success');
			void invalidateAll();
		} catch {
			toasts.show('Could not turn on notifications.', 'error');
		} finally {
			working = false;
		}
	}

	async function disable() {
		if (working) return;
		working = true;
		try {
			const reg = await navigator.serviceWorker.ready;
			const sub = await reg.pushManager.getSubscription();
			if (sub) {
				await apiFetch('/push/subscriptions', {
					method: 'DELETE',
					body: JSON.stringify({ endpoint: sub.endpoint })
				}).catch(() => {});
				await sub.unsubscribe();
			}
			subscribed = false;
			currentEndpoint = '';
			toasts.show('Reminders turned off on this device', 'info');
			void invalidateAll();
		} catch {
			toasts.show('Could not turn off notifications.', 'error');
		} finally {
			working = false;
		}
	}

	$effect(() => {
		if (form?.saved) toasts.show('Preferences saved', 'success');
		if (form?.tested) toasts.show('Test sent – check your notifications', 'success');
		if (form?.removed) toasts.show('Device removed', 'info');
		if (form?.error) toasts.show(form.error, 'error');
	});
</script>

<svelte:head><title>Notifications · CouplesCash</title></svelte:head>

<AppBar title="Notifications" back="/settings" />

<main class="flex flex-col gap-4 px-4 pt-2">
	{#if !data.vapidPublicKey}
		<EmptyState
			icon="bell"
			title="Notifications aren't set up yet"
			body="Push reminders need VAPID keys on the server. Whoever runs CouplesCash can generate them with `--vapid` and add them to the environment; this page lights up afterwards."
		>
			<p class="mt-2 text-xs text-base-content/50" data-testid="push-not-configured">
				Server reported: push not configured.
			</p>
		</EmptyState>
	{:else}
		<section class="card border border-base-300/60 bg-base-100 shadow-sm" aria-labelledby="device">
			<div class="card-body gap-3 p-5">
				<h2 id="device" class="flex items-center gap-2 font-semibold">
					<Icon name="bell-ring" size={18} /> This device
				</h2>
				{#if support === 'checking'}
					<div class="h-12 w-full skeleton rounded-xl"></div>
				{:else if support === 'insecure'}
					<p class="text-sm text-base-content/70">
						Notifications only work over HTTPS. Open CouplesCash through your tunnel address to turn
						them on.
					</p>
				{:else if support === 'ios-browser'}
					<div class="rounded-xl bg-base-200 p-4 text-sm">
						<p class="font-medium">Add to Home Screen first</p>
						<p class="mt-1 text-base-content/70">
							On iPhone, notifications are only available for installed web apps (iOS 16.4+). Tap
							<Icon name="share" size={14} class="inline align-text-bottom" label="Share" />
							<strong>Share</strong> → <strong>Add to Home Screen</strong>, then open CouplesCash
							from your home screen and come back here.
						</p>
					</div>
				{:else if support === 'unsupported'}
					<p class="text-sm text-base-content/70">This browser doesn't support web push.</p>
				{:else}
					<label class="label cursor-pointer justify-start gap-3 px-0">
						<input
							type="checkbox"
							class="toggle toggle-secondary"
							checked={subscribed}
							disabled={working || permission === 'denied'}
							onchange={(e) =>
								(e.currentTarget as HTMLInputElement).checked ? enable() : disable()}
						/>
						<span class="label-text">Remind me about bills on this device</span>
					</label>
					{#if permission === 'denied'}
						<p class="text-xs text-error">
							Notifications are blocked in your browser settings for this site.
						</p>
					{/if}
					{#if subscribed}
						<form method="POST" action="?/test" use:enhance>
							<button class="btn min-h-11 rounded-full border border-base-300 btn-ghost">
								<Icon name="send" size={16} /> Send a test
							</button>
						</form>
					{/if}
				{/if}
			</div>
		</section>

		{#if prefs}
			<section class="card border border-base-300/60 bg-base-100 shadow-sm" aria-labelledby="prefs">
				<form method="POST" action="?/prefs" class="card-body gap-4 p-5" use:enhance>
					<h2 id="prefs" class="font-semibold">What to send</h2>
					<label class="label cursor-pointer justify-start gap-3 px-0">
						<input
							type="checkbox"
							name="remindersEnabled"
							class="toggle toggle-secondary"
							defaultChecked={prefs.remindersEnabled}
						/>
						<span class="label-text">Bill reminders</span>
					</label>
					<div class="form-control">
						<span class="label-text mb-1 text-sm font-medium">When</span>
						<div class="flex flex-wrap gap-2" role="group" aria-label="Days before">
							{#each DAYS as d (d)}
								<label
									class="btn min-h-10 rounded-full border border-base-300 btn-ghost px-3 btn-sm has-checked:border-primary has-checked:bg-primary has-checked:text-primary-content"
								>
									<input
										type="checkbox"
										name="daysBefore"
										value={d}
										class="sr-only"
										defaultChecked={prefs.daysBefore.includes(d)}
									/>
									{dayLabel(d)}
								</label>
							{/each}
						</div>
					</div>
					<label class="form-control">
						<span class="label-text mb-1 text-sm font-medium">At</span>
						<select
							name="hourLocal"
							class="select-bordered select min-h-12 w-full"
							defaultValue={String(prefs.hourLocal)}
						>
							{#each Array.from({ length: 24 }, (_, h) => h) as h (h)}
								<option value={String(h)}
									>{h === 0
										? '12 AM'
										: h < 12
											? `${h} AM`
											: h === 12
												? '12 PM'
												: `${h - 12} PM`}</option
								>
							{/each}
						</select>
						<span class="label-text-alt mt-1 text-base-content/60"
							>In your household's time zone.</span
						>
					</label>
					<label class="label cursor-pointer justify-start gap-3 px-0">
						<input
							type="checkbox"
							name="activityEnabled"
							class="toggle toggle-secondary"
							defaultChecked={prefs.activityEnabled}
						/>
						<span class="label-text">Partner activity (payments, auto-pay, new expenses)</span>
					</label>
					<button class="btn min-h-12 rounded-full btn-primary">Save preferences</button>
				</form>
			</section>

			<section
				class="card border border-base-300/60 bg-base-100 shadow-sm"
				aria-labelledby="devices"
			>
				<div class="card-body gap-2 p-5">
					<h2 id="devices" class="font-semibold">Devices</h2>
					{#if prefs.subscriptions.length === 0}
						<p class="text-sm text-base-content/60">No devices are subscribed yet.</p>
					{:else}
						<ul class="divide-y divide-base-300/60 text-sm">
							{#each prefs.subscriptions as sub (sub.id)}
								<li class="flex items-center gap-3 py-2">
									<Icon name="smartphone" size={18} class="shrink-0 text-base-content/50" />
									<div class="min-w-0 flex-1">
										<p class="truncate">{sub.userAgent || 'Unknown device'}</p>
										<p class="text-xs text-base-content/50">
											Added {formatTimestamp(sub.createdAt)}{sub.lastUsedAt
												? ` · last used ${formatTimestamp(sub.lastUsedAt)}`
												: ''}
										</p>
									</div>
									<form method="POST" action="?/remove" use:enhance>
										<input type="hidden" name="id" value={sub.id} />
										{#if currentEndpoint && sub.userAgent === navigator.userAgent.slice(0, 200)}
											<input type="hidden" name="endpoint" value={currentEndpoint} />
										{/if}
										<button
											class="btn btn-circle min-h-11 min-w-11 btn-ghost btn-sm"
											aria-label="Remove device"
										>
											<Icon name="trash" size={16} />
										</button>
									</form>
								</li>
							{/each}
						</ul>
					{/if}
				</div>
			</section>
		{/if}
	{/if}
</main>
