import { expect, test } from '@playwright/test';
import { register } from './helpers';

test('theme toggle sets data-theme immediately and persists via cookie', async ({
	page,
	context
}) => {
	await register(page);
	await page.goto('/settings');
	await expect(page.locator('html')).not.toHaveAttribute('data-theme', /.+/);

	await page.getByTestId('theme-dark').click();
	await expect(page.locator('html')).toHaveAttribute('data-theme', 'couplescash-dark');
	await expect
		.poll(async () => (await context.cookies()).find((c) => c.name === 'theme')?.value)
		.toBe('dark');

	await page.reload();
	await expect(page.locator('html')).toHaveAttribute('data-theme', 'couplescash-dark');

	await page.getByTestId('theme-light').click();
	await expect(page.locator('html')).toHaveAttribute('data-theme', 'couplescash');
	await page.reload();
	await expect(page.locator('html')).toHaveAttribute('data-theme', 'couplescash');

	await page.getByTestId('theme-system').click();
	await expect(page.locator('html')).not.toHaveAttribute('data-theme', /.+/);
});

test('the browser chrome follows the theme: status bar colour and a matching manifest (the Android navigation bar)', async ({
	page,
	request
}) => {
	await register(page);
	await page.goto('/settings');
	// system: both colours by media query, and the manifest of the phone's scheme (light here)
	await expect(page.locator('meta[name="theme-color"]')).toHaveCount(2);
	await expect(page.locator('link[rel="manifest"]')).toHaveAttribute(
		'href',
		'/manifest.webmanifest?scheme=light'
	);

	await page.getByTestId('theme-dark').click();
	await expect(page.locator('meta[name="theme-color"]')).toHaveCount(1);
	await expect(page.locator('meta[name="theme-color"]')).toHaveAttribute('content', '#1c2130');
	await expect(page.locator('link[rel="manifest"]')).toHaveAttribute(
		'href',
		'/manifest.webmanifest?scheme=dark'
	);
	await page.reload();
	await expect(page.locator('meta[name="theme-color"]')).toHaveAttribute('content', '#1c2130');
	await expect(page.locator('link[rel="manifest"]')).toHaveAttribute(
		'href',
		'/manifest.webmanifest?scheme=dark'
	);

	const dark = await (await request.get('/manifest.webmanifest?scheme=dark')).json();
	expect([dark.theme_color, dark.background_color, dark.start_url]).toEqual([
		'#1c2130',
		'#1c2130',
		'/week'
	]);
	const plain = await (await request.get('/manifest.webmanifest')).json();
	expect(plain.background_color).toBe('#fdf8f3');
	expect(plain.user_preferences.color_scheme.dark.background_color).toBe('#1c2130');
});
