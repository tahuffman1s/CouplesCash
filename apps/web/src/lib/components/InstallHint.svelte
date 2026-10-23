<script lang="ts">
	import { PUBLIC_APP_NAME } from '$app/env/public';
	import Icon from './Icon.svelte';

	type BeforeInstallPromptEvent = Event & {
		prompt: () => Promise<void>;
		userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>;
	};

	let standalone = $state(false);
	let platform = $state<'ios' | 'android' | 'other'>('other');
	let deferred = $state<BeforeInstallPromptEvent | null>(null);
	let installed = $state(false);

	$effect(() => {
		const nav = navigator as Navigator & { standalone?: boolean };
		standalone = window.matchMedia('(display-mode: standalone)').matches || nav.standalone === true;
		const ua = navigator.userAgent;
		const iPadOS = navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1;
		if (/iPhone|iPad|iPod/.test(ua) || iPadOS) platform = 'ios';
		else if (/Android/.test(ua)) platform = 'android';

		const onPrompt = (e: Event) => {
			e.preventDefault();
			deferred = e as BeforeInstallPromptEvent;
		};
		const onInstalled = () => (installed = true);
		window.addEventListener('beforeinstallprompt', onPrompt);
		window.addEventListener('appinstalled', onInstalled);
		return () => {
			window.removeEventListener('beforeinstallprompt', onPrompt);
			window.removeEventListener('appinstalled', onInstalled);
		};
	});

	async function install() {
		if (!deferred) return;
		await deferred.prompt();
		const choice = await deferred.userChoice;
		if (choice.outcome === 'accepted') installed = true;
		deferred = null;
	}
</script>

{#if standalone || installed}
	<div class="flex items-center gap-3 text-sm">
		<Icon name="circle-check" size={20} class="text-success" />
		<span>{PUBLIC_APP_NAME} is installed on this device.</span>
	</div>
{:else if deferred}
	<div class="flex items-center justify-between gap-3">
		<p class="text-sm">
			Add {PUBLIC_APP_NAME} to your home screen for a full-screen, app-like experience.
		</p>
		<button type="button" class="btn min-h-11 rounded-full btn-secondary" onclick={install}>
			<Icon name="download" size={18} /> Install
		</button>
	</div>
{:else if platform === 'ios'}
	<ol class="list-inside list-decimal space-y-1.5 text-sm">
		<li>Open this page in <strong>Safari</strong>.</li>
		<li>
			Tap the <Icon name="share" size={16} class="inline align-text-bottom" label="Share" />
			<strong>Share</strong> button.
		</li>
		<li>Choose <strong>Add to Home Screen</strong>, then <strong>Add</strong>.</li>
	</ol>
{:else if platform === 'android'}
	<ol class="list-inside list-decimal space-y-1.5 text-sm">
		<li>Open this page in <strong>Chrome</strong>.</li>
		<li>Tap the <strong>⋮</strong> menu in the top right.</li>
		<li>Choose <strong>Add to Home screen</strong> or <strong>Install app</strong>.</li>
	</ol>
{:else}
	<p class="text-sm">
		On your phone, open this site in Safari (iPhone) or Chrome (Android) and choose
		<strong>Add to Home Screen</strong> to install {PUBLIC_APP_NAME}.
	</p>
{/if}
