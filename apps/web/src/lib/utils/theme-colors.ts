/** The browser-chrome colours of each theme (status bar, Android navigation bar, splash): the themes' base-200. */
export type Scheme = 'light' | 'dark';

export const THEME_COLORS: Record<Scheme, string> = {
	light: '#fdf8f3',
	dark: '#1c2130'
};

export function manifestColors(scheme: Scheme): { theme_color: string; background_color: string } {
	return { theme_color: THEME_COLORS[scheme], background_color: THEME_COLORS[scheme] };
}

/**
 * The `<head>` tags for the theme: the status bar colour (one colour for a chosen theme, both media variants for "system") and the
 * manifest matching it.
 */
export function themeHead(theme: 'light' | 'dark' | 'system'): string {
	if (theme === 'system')
		return [
			`<meta name="theme-color" media="(prefers-color-scheme: light)" content="${THEME_COLORS.light}" />`,
			`<meta name="theme-color" media="(prefers-color-scheme: dark)" content="${THEME_COLORS.dark}" />`,
			`<link rel="manifest" href="/manifest.webmanifest" />`
		].join('\n\t\t');
	return [
		`<meta name="theme-color" content="${THEME_COLORS[theme]}" />`,
		`<link rel="manifest" href="/manifest.webmanifest?scheme=${theme}" />`
	].join('\n\t\t');
}

/** Browser only: after the theme changes in Settings, the status bar colour and the linked manifest follow without a reload. */
export function applyThemeChrome(theme: 'light' | 'dark' | 'system'): void {
	const head = document.head;
	for (const m of head.querySelectorAll('meta[name="theme-color"]')) m.remove();
	const metas: [Scheme, string | null][] =
		theme === 'system'
			? [
					['light', '(prefers-color-scheme: light)'],
					['dark', '(prefers-color-scheme: dark)']
				]
			: [[theme, null]];
	for (const [scheme, media] of metas) {
		const meta = document.createElement('meta');
		meta.name = 'theme-color';
		meta.content = THEME_COLORS[scheme];
		if (media) meta.media = media;
		head.append(meta);
	}
	const link = head.querySelector<HTMLLinkElement>('link[rel="manifest"]');
	const scheme: Scheme =
		theme === 'system'
			? matchMedia('(prefers-color-scheme: dark)').matches
				? 'dark'
				: 'light'
			: theme;
	if (link) link.href = `/manifest.webmanifest?scheme=${scheme}`;
}
