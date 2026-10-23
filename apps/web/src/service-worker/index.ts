import { self as sw } from '$app/service-worker';
import { assets, immutable, prerendered } from '$app/manifest';
import { version } from '$app/env';

const CACHE = `couplescash-${version}`;
const OFFLINE = '/offline';
const PRECACHE = [
	...immutable.map((f) => f.path),
	...assets.map((f) => f.path),
	...prerendered.map((p) => p.path)
];
const PRECACHED = new Set(PRECACHE);

sw.addEventListener('install', (event) => {
	event.waitUntil(
		(async () => {
			const cache = await caches.open(CACHE);
			await cache.addAll(PRECACHE);
			await sw.skipWaiting();
		})()
	);
});

sw.addEventListener('activate', (event) => {
	event.waitUntil(
		(async () => {
			for (const key of await caches.keys()) {
				if (key !== CACHE) await caches.delete(key);
			}
			await sw.clients.claim();
		})()
	);
});

sw.addEventListener('fetch', (event) => {
	const { request } = event;
	if (request.method !== 'GET') return;
	const url = new URL(request.url);
	if (url.origin !== sw.location.origin) return;
	// Never cache the BFF proxy or SvelteKit data requests
	if (url.pathname.startsWith('/api/') || url.pathname.endsWith('/__data.json')) return;

	// App shell & static assets: cache-first
	if (PRECACHED.has(url.pathname)) {
		event.respondWith(
			(async () => {
				const cache = await caches.open(CACHE);
				const hit = await cache.match(request);
				if (hit) return hit;
				const res = await fetch(request);
				if (res.ok) void cache.put(request, res.clone());
				return res;
			})()
		);
		return;
	}

	// Navigations: network-first with the offline page as a fallback
	if (request.mode === 'navigate') {
		event.respondWith(
			(async () => {
				try {
					return await fetch(request);
				} catch {
					const cache = await caches.open(CACHE);
					return (await cache.match(OFFLINE)) ?? Response.error();
				}
			})()
		);
	}
});

// ---- Web Push --------------------------------------------------------------------------------
type PushPayload = { title?: string; body?: string; url?: string; tag?: string; icon?: string };

sw.addEventListener('push', (event) => {
	let data: PushPayload;
	try {
		data = (event.data?.json() as PushPayload) ?? {};
	} catch {
		data = { body: event.data?.text() ?? '' };
	}
	const title = data.title ?? 'CouplesCash';
	event.waitUntil(
		sw.registration.showNotification(title, {
			body: data.body ?? '',
			icon: data.icon ?? '/icons/icon-192.png',
			badge: '/icons/icon-192.png',
			tag: data.tag,
			data: { url: data.url ?? '/' }
		})
	);
});

sw.addEventListener('notificationclick', (event) => {
	event.notification.close();
	const target = new URL(
		(event.notification.data as { url?: string })?.url ?? '/',
		sw.location.origin
	).href;
	event.waitUntil(
		(async () => {
			const all = await sw.clients.matchAll({ type: 'window', includeUncontrolled: true });
			for (const client of all) {
				if ('focus' in client) {
					await client.focus();
					if ('navigate' in client && client.url !== target) {
						try {
							await client.navigate(target);
						} catch {
							// cross-origin or unsupported – fall through to openWindow
						}
					}
					return;
				}
			}
			await sw.clients.openWindow(target);
		})()
	);
});
