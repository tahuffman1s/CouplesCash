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
