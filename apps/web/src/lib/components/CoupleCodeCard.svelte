<script lang="ts">
	import { onMount } from 'svelte';
	import type { CoupleCode } from '#lib/types.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';
	import Icon from './Icon.svelte';

	let { code }: { code: CoupleCode } = $props();

	let container = $state<HTMLDivElement | null>(null);
	let now = $state(Date.now());
	let copied = $state(false);
	let rendered = $state(false);

	const remainingMs = $derived(Math.max(0, new Date(code.expiresAt).getTime() - now));
	const expired = $derived(remainingMs <= 0);
	const mm = $derived(Math.floor(remainingMs / 60_000));
	const ss = $derived(Math.floor((remainingMs % 60_000) / 1000));
	const joinUrl = $derived(`${location.origin}/couple/join?c=${encodeURIComponent(code.payload)}`);

	// Small heart, tinted with the theme's primary, placed in the centre of the QR
	function heartDataUrl(colour: string): string {
		const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64"><circle cx="32" cy="32" r="32" fill="white"/><path d="M32 52C18 42 10 35 10 25c0-7 5-12 11-12 4.5 0 8 2.5 11 6 3-3.5 6.5-6 11-6 6 0 11 5 11 12 0 10-8 17-22 27z" fill="${colour}"/></svg>`;
		return `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`;
	}

	onMount(() => {
		const tick = setInterval(() => (now = Date.now()), 1000);
		let cancelled = false;
		(async () => {
			const { default: QRCodeStyling } = await import('qr-code-styling');
			if (cancelled || !container) return;
			const styles = getComputedStyle(document.documentElement);
			const primary = styles.getPropertyValue('--color-primary').trim() || '#e1555f';
			const ink = styles.getPropertyValue('--color-base-content').trim() || '#222';
			const qr = new QRCodeStyling({
				width: 232,
				height: 232,
				type: 'svg',
				data: joinUrl,
				margin: 4,
				qrOptions: { errorCorrectionLevel: 'Q' },
				dotsOptions: { type: 'rounded', color: ink },
				cornersSquareOptions: { type: 'extra-rounded', color: primary },
				cornersDotOptions: { type: 'dot', color: primary },
				backgroundOptions: { color: 'transparent' },
				image: heartDataUrl(primary),
				imageOptions: { imageSize: 0.32, margin: 4, hideBackgroundDots: true }
			});
			qr.append(container);
			rendered = true;
		})();
		return () => {
			cancelled = true;
			clearInterval(tick);
		};
	});

	async function copy() {
		try {
			await navigator.clipboard.writeText(code.code);
			copied = true;
			toasts.show('Code copied', 'success');
			setTimeout(() => (copied = false), 2000);
		} catch {
			toasts.show('Copy not available – read the code aloud instead', 'info');
		}
	}

	async function share() {
		const text = `Join me on CouplesCash! Open ${joinUrl} or enter code ${code.code}`;
		if (navigator.share) {
			try {
				await navigator.share({ title: 'CouplesCash', text, url: joinUrl });
			} catch {
				// cancelled
			}
		} else {
			await copy();
		}
	}
</script>

<div class="flex flex-col items-center gap-4">
	<div
		class="relative rounded-3xl bg-base-100 p-3 shadow-inner ring-1 ring-base-300/70 {expired
			? 'opacity-30'
			: ''}"
		aria-label="Couple Code QR"
		role="img"
	>
		<div class="relative h-[232px] w-[232px]">
			{#if !rendered}
				<div class="absolute inset-0 skeleton rounded-2xl"></div>
			{/if}
			<div bind:this={container} class="h-full w-full"></div>
		</div>
	</div>

	<div class="text-center">
		<p class="text-xs tracking-wide text-base-content/60 uppercase">Or type this code</p>
		<p class="mt-1 text-4xl font-bold tracking-[0.18em] tabular" data-testid="couple-code">
			{code.code}
		</p>
	</div>

	<div class="flex gap-2">
		<button
			type="button"
			class="btn min-h-11 rounded-full border border-base-300 btn-ghost"
			onclick={copy}
		>
			<Icon name={copied ? 'check' : 'copy'} size={18} />
			{copied ? 'Copied' : 'Copy'}
		</button>
		<button
			type="button"
			class="btn min-h-11 rounded-full border border-base-300 btn-ghost"
			onclick={share}
		>
			<Icon name="share" size={18} /> Share
		</button>
	</div>

	<p class="text-sm text-base-content/60" aria-live="polite" data-testid="countdown">
		{#if expired}
			This code has expired.
		{:else}
			Expires in <span class="font-medium tabular">{mm}:{ss < 10 ? `0${ss}` : ss}</span>
		{/if}
	</p>
</div>
