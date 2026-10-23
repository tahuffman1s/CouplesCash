// Generates PWA icons (PNG) from an inline SVG using sharp. Run: pnpm icons
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import sharp from 'sharp';

const out = path.resolve(import.meta.dirname, '../static/icons');
await mkdir(out, { recursive: true });

/** @param {boolean} maskable - extra padding so the heart sits inside the safe zone */
function svg(maskable) {
	const pad = maskable ? 0.22 : 0.12; // fraction of the canvas
	const s = 512;
	const inner = s * (1 - pad * 2);
	const x = s * pad;
	const heart = `M256 440 C 130 350, 64 290, 64 200 C 64 140, 110 100, 164 100 C 203 100, 236 122, 256 156 C 276 122, 309 100, 348 100 C 402 100, 448 140, 448 200 C 448 290, 382 350, 256 440 Z`;
	return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${s} ${s}" width="${s}" height="${s}">
	<defs>
		<linearGradient id="g" x1="0" y1="0" x2="1" y2="1">
			<stop offset="0" stop-color="#f4866f"/>
			<stop offset="1" stop-color="#e1555f"/>
		</linearGradient>
	</defs>
	<rect width="${s}" height="${s}" rx="${maskable ? 0 : 112}" fill="url(#g)"/>
	<g transform="translate(${x} ${x}) scale(${inner / s})">
		<path d="${heart}" fill="#fff" opacity="0.96"/>
		<text x="256" y="300" text-anchor="middle" font-family="system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif" font-weight="700" font-size="190" fill="#e1555f">$</text>
	</g>
</svg>`;
}

await writeFile(path.join(out, 'icon.svg'), svg(false));

/** @type {[string, number, boolean][]} */
const jobs = [
	['icon-192.png', 192, false],
	['icon-512.png', 512, false],
	['maskable-192.png', 192, true],
	['maskable-512.png', 512, true],
	['apple-touch-icon.png', 180, true]
];
for (const [name, size, maskable] of jobs) {
	await sharp(Buffer.from(svg(maskable)))
		.resize(size, size)
		.png()
		.toFile(path.join(out, name));
	console.log('wrote', name);
}
