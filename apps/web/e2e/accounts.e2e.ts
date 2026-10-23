import { expect, test } from '@playwright/test';
import { registerWithHousehold } from './helpers';

test('create an account: it appears in the list and in net worth', async ({ page }) => {
	await registerWithHousehold(page);
	await page.goto('/accounts');
	await expect(page.getByText('No accounts yet')).toBeVisible();
	await page.getByRole('link', { name: 'Add your first account' }).click();
	await expect(page).toHaveURL(/\/accounts\/new$/);

	await page.getByLabel('Account name').fill('Everyday checking');
	await page.getByLabel('Bank or institution').fill('Chase');
	await page.getByRole('radiogroup', { name: 'Account type' }).getByText('Checking').click();
	await page.getByLabel('Current balance').fill('1,250.50');
	await page.getByRole('button', { name: 'Save account' }).click();

	await expect(page).toHaveURL(/\/accounts\/[0-9a-f-]+/);
	await expect(page.getByTestId('balance')).toHaveText('$1,250.50');

	// quick balance update through the proxy
	await page.getByLabel('New balance').fill('1300');
	await page.getByRole('button', { name: 'Update' }).click();
	await expect(page.getByTestId('balance')).toHaveText('$1,300.00');

	await page.goto('/accounts');
	await expect(page.getByRole('link', { name: /Everyday checking/ })).toBeVisible();
	await expect(page.getByTestId('net-worth')).toHaveText('$1,300.00');

	// and in the dashboard hero
	await page.goto('/');
	await expect(page.getByText('Net worth')).toBeVisible();
	await expect(page.getByText('$1,300', { exact: false }).first()).toBeVisible();
});
