/** Base64url VAPID public key → the `applicationServerKey` bytes `pushManager.subscribe` wants. */
export function urlBase64ToUint8Array(base64Url: string): Uint8Array {
	const padding = '='.repeat((4 - (base64Url.length % 4)) % 4);
	const base64 = (base64Url + padding).replace(/-/g, '+').replace(/_/g, '/');
	const raw =
		typeof atob === 'function' ? atob(base64) : Buffer.from(base64, 'base64').toString('binary');
	const out = new Uint8Array(raw.length);
	for (let i = 0; i < raw.length; i++) out[i] = raw.charCodeAt(i);
	return out;
}

export type PushSupport = 'supported' | 'insecure' | 'ios-browser' | 'unsupported';

/** Web Push works in secure contexts with a service worker; on iOS only once installed. */
export function detectPushSupport(): PushSupport {
	if (typeof window === 'undefined') return 'unsupported';
	if (!window.isSecureContext) return 'insecure';
	const nav = navigator as Navigator & { standalone?: boolean };
	const ios = /iPhone|iPad|iPod/.test(navigator.userAgent);
	const standalone =
		window.matchMedia('(display-mode: standalone)').matches || nav.standalone === true;
	if (ios && !standalone) return 'ios-browser';
	if (!('serviceWorker' in navigator) || !('PushManager' in window) || !('Notification' in window))
		return 'unsupported';
	return 'supported';
}

/** Serialises a PushSubscription into the body `POST /push/subscriptions` expects. */
export function subscriptionBody(sub: PushSubscription) {
	const json = sub.toJSON();
	return {
		endpoint: sub.endpoint,
		keys: { p256dh: json.keys?.p256dh ?? '', auth: json.keys?.auth ?? '' },
		userAgent: navigator.userAgent.slice(0, 200)
	};
}
