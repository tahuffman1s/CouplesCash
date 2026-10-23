<script lang="ts">
	import { onMount } from 'svelte';
	import Icon from './Icon.svelte';

	let {
		onResult,
		active = true
	}: {
		/** called once with the decoded text (payload or deep link) */
		onResult: (text: string) => void;
		active?: boolean;
	} = $props();

	let video = $state<HTMLVideoElement | null>(null);
	let status = $state<'starting' | 'scanning' | 'denied' | 'unavailable'>('starting');
	let message = $state('');

	onMount(() => {
		let stream: MediaStream | null = null;
		let raf = 0;
		let stopped = false;
		const canvas = document.createElement('canvas');
		const ctx = canvas.getContext('2d', { willReadFrequently: true });

		(async () => {
			if (!navigator.mediaDevices?.getUserMedia) {
				status = 'unavailable';
				message = 'Camera scanning is not available in this browser.';
				return;
			}
			const { default: jsQR } = await import('jsqr');
			try {
				stream = await navigator.mediaDevices.getUserMedia({
					video: {
						facingMode: { ideal: 'environment' },
						width: { ideal: 1280 },
						height: { ideal: 720 }
					},
					audio: false
				});
			} catch (e) {
				const name = (e as DOMException)?.name;
				status = name === 'NotAllowedError' || name === 'SecurityError' ? 'denied' : 'unavailable';
				message =
					status === 'denied'
						? 'Camera access was denied. Allow it in your browser settings, or type the code instead.'
						: 'No camera could be started. Type the code instead.';
				return;
			}
			if (stopped || !video) return;
			video.srcObject = stream;
			await video.play();
			status = 'scanning';

			const scan = () => {
				if (stopped || !video || !ctx) return;
				if (video.readyState === video.HAVE_ENOUGH_DATA) {
					const w = (canvas.width = video.videoWidth);
					const h = (canvas.height = video.videoHeight);
					ctx.drawImage(video, 0, 0, w, h);
					const img = ctx.getImageData(0, 0, w, h);
					const found = jsQR(img.data, w, h, { inversionAttempts: 'dontInvert' });
					if (found?.data) {
						stopped = true;
						onResult(found.data);
						return;
					}
				}
				raf = requestAnimationFrame(scan);
			};
			raf = requestAnimationFrame(scan);
		})();

		return () => {
			stopped = true;
			cancelAnimationFrame(raf);
			stream?.getTracks().forEach((t) => t.stop());
		};
	});
</script>

<div class="flex flex-col items-center gap-3">
	{#if status === 'starting' || status === 'scanning'}
		<div class="relative aspect-square w-full max-w-xs overflow-hidden rounded-3xl bg-base-300">
			<video
				bind:this={video}
				playsinline
				muted
				class="h-full w-full object-cover {active ? '' : 'hidden'}"
			></video>
			<div
				class="pointer-events-none absolute inset-6 rounded-2xl border-2 border-white/80 shadow-[0_0_0_999px_rgba(0,0,0,0.25)]"
				aria-hidden="true"
			></div>
			{#if status === 'starting'}
				<div class="absolute inset-0 flex items-center justify-center">
					<span class="loading loading-lg loading-spinner text-primary"></span>
				</div>
			{/if}
		</div>
		<p class="text-sm text-base-content/60">Point the camera at your partner's Couple Code.</p>
	{:else}
		<div class="alert text-sm" role="status">
			<Icon name="camera-off" size={20} />
			<span>{message}</span>
		</div>
	{/if}
</div>
