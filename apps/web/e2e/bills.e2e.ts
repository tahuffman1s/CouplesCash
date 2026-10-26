import { expect, test } from '@playwright/test';
import { registerWithHousehold, shiftDays, todayIso } from './helpers';

test('bills: grouped by pay week with chips; a bill can be marked paid on its page', async ({
	page
}) => {
	await registerWithHousehold(page);
	const today = todayIso();

	await page.goto('/bills/new');
	await page.getByRole('radiogroup', { name: 'Kind' }).getByText('Bill', { exact: true }).click();
	await page.getByLabel('Name', { exact: true }).fill('Rent');
	await page.getByLabel('Amount').fill('1800');
	await page.getByLabel('Repeats').selectOption('monthly');
	await page.getByLabel('First due date').fill(today);
	await page.getByLabel('Category').selectOption({ label: 'Housing' });
	await page.getByRole('button', { name: 'Save', exact: true }).click();
	await expect(page).toHaveURL(/\/bills\/[0-9a-f-]+/);
	await expect(page.getByRole('heading', { name: 'Rent' })).toBeVisible();

	// a yearly bill far out lands under "Later"
	await page.goto('/bills/new');
	await page.getByLabel('Name', { exact: true }).fill('Car insurance');
	await page.getByLabel('Amount').fill('432');
	await page.getByLabel('Repeats').selectOption('yearly');
	await page.getByLabel('First due date').fill(shiftDays(today, 90));
	await page.getByRole('button', { name: 'Save', exact: true }).click();
	await expect(page).toHaveURL(/\/bills\/[0-9a-f-]+/);

	await page.goto('/bills');
	const week = page.locator('[data-group="week"]');
	await expect(week.getByRole('heading', { name: 'This pay week' })).toBeVisible();
	await expect(week.getByTestId('bill-row').filter({ hasText: 'Rent' })).toBeVisible();
	await expect(
		page
			.locator('[data-group="later"]')
			.getByTestId('bill-row')
			.filter({ hasText: 'Car insurance' })
	).toBeVisible();
	await expect(page.getByRole('link', { name: /Calendar/ })).toHaveCount(0);
	await page.getByRole('radio', { name: 'Income' }).click();
	await expect(page.getByTestId('bills-none')).toBeVisible();
	await page.getByRole('radio', { name: 'Transfers' }).click();
	await expect(page.getByTestId('bills-none')).toBeVisible();
	await page.getByRole('radio', { name: 'All' }).click();

	// the bill's page: tick it paid, it stays paid, untick
	await page.getByTestId('bill-row').filter({ hasText: 'Rent' }).click();
	const row = page.getByTestId('occurrence-row').filter({ hasText: 'Rent' }).first();
	await row.getByRole('button', { name: 'Mark Rent paid' }).click();
	await expect(row.getByRole('button', { name: 'Mark Rent unpaid' })).toBeVisible();
	await page.reload();
	const again = page.getByTestId('occurrence-row').first();
	await again.getByRole('button', { name: 'Mark Rent unpaid' }).click();
	await expect(again.getByRole('button', { name: 'Mark Rent paid' })).toBeVisible();
});
