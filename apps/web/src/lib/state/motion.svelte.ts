import { browser } from '$app/env';

/** True when the OS asks for reduced motion. Safe to read during SSR (false). */
export function prefersReducedMotion(): boolean {
	return browser ? window.matchMedia('(prefers-reduced-motion: reduce)').matches : false;
}
