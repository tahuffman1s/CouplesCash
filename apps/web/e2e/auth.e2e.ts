import { expect, test } from '@playwright/test';
import { register } from './helpers';

test('unauthenticated / redirects to /login', async ({ page }) => {
	await page.goto('/');
	await expect(page).toHaveURL(/\/login$/);
	await expect(page.getByRole('heading', { name: 'Welcome back' })).toBeVisible();
});

test('register lands on the dashboard empty state', async ({ page }) => {
	await register(page, 'Sam');
	await expect(
		page.getByRole('heading', { name: /Good (morning|afternoon|evening), Sam/ })
	).toBeVisible();
	await expect(page.getByText('Start your shared household')).toBeVisible();
	await expect(page.getByRole('link', { name: 'Set up our household' })).toBeVisible();
	// bottom dock is present with the active item marked
	await expect(
		page.getByRole('navigation', { name: 'Primary' }).getByRole('link', { name: 'Home' })
	).toHaveAttribute('aria-current', 'page');
});

test('/api without the X-Requested-With header is forbidden', async ({ page }) => {
	await register(page);
	const res = await page.request.get('/api/accounts');
	expect(res.status()).toBe(403);
	const ok = await page.request.get('/api/couple', {
		headers: { 'X-Requested-With': 'CouplesCash' }
	});
	expect(ok.status()).toBe(200);
});

test('session survives an expired access cookie via refresh', async ({ page, context }) => {
	await register(page);
	const cookies = await context.cookies();
	const access = cookies.find((c) => c.name === 'cc_access');
	const refresh = cookies.find((c) => c.name === 'cc_refresh');
	expect(access?.httpOnly).toBe(true);
	expect(refresh?.httpOnly).toBe(true);
	// drop the access cookie: the BFF must transparently refresh instead of bouncing to /login
	await context.clearCookies({ name: 'cc_access' });
	await page.goto('/settings');
	await expect(page).toHaveURL(/\/settings$/);
	const after = await context.cookies();
	expect(after.find((c) => c.name === 'cc_access')).toBeTruthy();
});
