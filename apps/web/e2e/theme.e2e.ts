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
