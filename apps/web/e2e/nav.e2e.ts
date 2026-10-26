import { expect, test } from '@playwright/test';
import { register, registerWithHousehold, uniqueEmail } from './helpers';

test('three tabs and a gear; no More, no Money', async ({ page }) => {
	await registerWithHousehold(page);
	for (const [path, tab] of [
		['/week', 'This week'],
		['/transactions', 'Transactions'],
		['/bills', 'Bills']
	] as const) {
		await page.goto(path);
		const dock = page.getByRole('navigation', { name: 'Primary' });
		await expect(dock.getByRole('link')).toHaveCount(3);
		await expect(dock.getByRole('link', { name: new RegExp(tab) })).toHaveAttribute(
			'aria-current',
			'page'
		);
		await expect(page.getByTestId('settings-gear')).toBeVisible();
	}
	await page.getByTestId('settings-gear').click();
	await expect(page).toHaveURL(/\/settings$/);
	for (const id of ['you', 'household', 'accounts', 'banks'])
		await expect(page.locator(`section#${id}`)).toBeVisible();
	// the removed settings are gone
	await expect(page.getByText('Week starts on')).toHaveCount(0);
	await expect(page.getByText('Pay period')).toHaveCount(0);
	await expect(page.getByText('Currency')).toHaveCount(0);
	await expect(page.getByText('Partner activity')).toHaveCount(0);
});

test('old URLs land on their new home', async ({ page }) => {
	await registerWithHousehold(page);
	for (const from of [
		'/accounts',
		'/insights',
		'/insights/report',
		'/goals',
		'/goals/new',
		'/todos',
		'/activity',
		'/expenses/new',
		'/more',
		'/payperiod',
		'/pending',
		'/transfers'
	]) {
		await page.goto(from);
		await expect(page, from).toHaveURL(/\/week$/);
	}
	await page.goto('/calendar?m=2026-01');
	await expect(page).toHaveURL(/\/bills$/);
	await page.goto('/inbox');
	await expect(page).toHaveURL(/\/transactions$/);
	await page.goto('/settings/household');
	await expect(page).toHaveURL(/\/settings#household$/);
	await page.goto('/settings/notifications');
	await expect(page).toHaveURL(/\/settings#you$/);
	// kept detail routes
	await page.goto('/accounts/new');
	await expect(page.getByRole('heading', { name: 'New account' })).toBeVisible();
	await page.goto('/imports/csv');
	await expect(page).toHaveURL(/\/imports\/csv$/);
});

test('notifications and passkeys degrade gracefully when the server has neither', async ({
	page
}) => {
	await registerWithHousehold(page);
	await page.goto('/settings#you');
	await expect(page.getByTestId('push-not-configured')).toBeVisible();
	await page.getByRole('link', { name: /Passkeys/ }).click();
	await expect(page).toHaveURL(/\/settings\/passkeys$/);
});

test('notifications: just the payday push and bill reminders', async ({ page }) => {
	// `push-` users get a VAPID key from the mock, so the prefs form shows
	await register(page, 'Alex', uniqueEmail('push'));
	await page.goto('/settings#you');
	const toggle = page.getByTestId('payday-push-toggle');
	await expect(toggle).toBeChecked();
	await expect(page.getByText('Bill reminders')).toBeVisible();
	await expect(page.getByText(/Partner activity/)).toHaveCount(0);
	await expect(page.getByText(/Weekly summary/)).toHaveCount(0);
	await toggle.uncheck();
	await page.getByRole('button', { name: 'Save preferences' }).click();
	await expect(page.getByText('Preferences saved')).toBeVisible();
	await page.reload();
	await expect(page.getByTestId('payday-push-toggle')).not.toBeChecked();
});
