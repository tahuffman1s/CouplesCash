import { expect, test } from '@playwright/test';
import { register } from './helpers';

test('notifications and passkeys degrade gracefully when the server has neither', async ({
	page
}) => {
	// login page: no passkey button when the API reports passkeys disabled
	await page.goto('/login');
	await expect(page.getByRole('heading', { name: 'Welcome back' })).toBeVisible();
	await expect(page.getByTestId('passkey-login')).toHaveCount(0);

	await register(page);
	await page.goto('/settings');
	await page.getByRole('link', { name: /Notifications/ }).click();
	await expect(page).toHaveURL(/\/settings\/notifications$/);
	await expect(page.getByText("Notifications aren't set up yet")).toBeVisible();
	await expect(page.getByTestId('push-not-configured')).toBeVisible();

	await page.goto('/settings');
	await page.getByRole('link', { name: /Passkeys/ }).click();
	await expect(page).toHaveURL(/\/settings\/passkeys$/);
	await expect(page.getByTestId('passkeys-disabled')).toBeVisible();
	await expect(page.getByRole('button', { name: 'Add a passkey' })).toHaveCount(0);

	// the BFF passkey endpoint refuses requests without the anti-CSRF marker
	const res = await page.request.get('/login/passkey');
	expect(res.status()).toBe(403);
});
