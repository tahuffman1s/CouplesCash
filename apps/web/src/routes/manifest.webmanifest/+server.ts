import type { RequestHandler } from './$types';
import { manifestColors, type Scheme } from '#lib/utils/theme-colors.ts';

/**
 * The web app manifest, its colours following the theme. Android takes the installed app's navigation bar (and splash) colour from
 * here, so a dark app needs a dark manifest: `?scheme=dark|light` (the page links the one matching the theme it shows); without it,
 * light with the dark variant under `user_preferences` for browsers that read it.
 */
const BASE = {
	name: 'CouplesCash',
	short_name: 'CouplesCash',
	description: 'Your shared money for two: what is left this week, every bill, every transaction.',
	id: '/',
	start_url: '/week',
	scope: '/',
	display: 'standalone',
	orientation: 'portrait',
	lang: 'en',
	categories: ['finance', 'lifestyle'],
	icons: [
		{
			src: '/icons/icon-192.png',
			sizes: '192x192',
			type: 'image/png',
			purpose: 'any'
		},
		{
			src: '/icons/icon-512.png',
			sizes: '512x512',
			type: 'image/png',
			purpose: 'any'
		},
		{
			src: '/icons/maskable-192.png',
			sizes: '192x192',
			type: 'image/png',
			purpose: 'maskable'
		},
		{
			src: '/icons/maskable-512.png',
			sizes: '512x512',
			type: 'image/png',
			purpose: 'maskable'
		},
		{
			src: '/icons/icon.svg',
			sizes: 'any',
			type: 'image/svg+xml',
			purpose: 'any'
		}
	],
	shortcuts: [
		{
			name: 'Add a bill',
			url: '/bills/new',
			icons: [
				{
					src: '/icons/icon-192.png',
					sizes: '192x192'
				}
			]
		},
		{
			name: 'Transactions',
			url: '/transactions',
			icons: [
				{
					src: '/icons/icon-192.png',
					sizes: '192x192'
				}
			]
		}
	]
};

export const GET: RequestHandler = ({ url }) => {
	const param = url.searchParams.get('scheme');
	const scheme: Scheme | null = param === 'dark' || param === 'light' ? param : null;
	const colors = manifestColors(scheme ?? 'light');
	const dark = manifestColors('dark');
	const body = {
		...BASE,
		...colors,
		...(scheme === null ? { user_preferences: { color_scheme: { dark } } } : {})
	};
	return new Response(JSON.stringify(body), {
		headers: {
			'content-type': 'application/manifest+json; charset=utf-8',
			'cache-control': 'public, max-age=3600'
		}
	});
};
