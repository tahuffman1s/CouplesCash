import { expect, test } from '@playwright/test';
import { addAccount, registerWithHousehold, todayIso } from './helpers';

test('activity feed names the actor for each action just performed', async ({ page }) => {
	await registerWithHousehold(page, 'Riley');
	await addAccount(page, 'Joint checking', '500');

	await page.goto('/bills/new');
	await page.getByLabel('Name').fill('Electric');
	await page.getByLabel('Amount').fill('142.50');
	await page.getByLabel('First due date').fill(todayIso());
	await page.getByRole('button', { name: 'Save', exact: true }).click();
	await expect(page).toHaveURL(/\/bills\/[0-9a-f-]+/);
	// the detail page lists several upcoming occurrences; pay the first (today's)
	// the toggle is optimistic: wait for the API to confirm before navigating away
	const saved = page.waitForResponse(
		(r) => /\/occurrences\/[^/]+\/paid$/.test(r.url()) && r.request().method() === 'PUT' && r.ok()
	);
	await page.getByRole('button', { name: 'Mark Electric paid' }).first().click();
	await saved;
	await expect(page.getByRole('button', { name: 'Mark Electric unpaid' })).toBeVisible();

	// More › Activity
	await page.goto('/more');
	await page.getByRole('link', { name: /Activity/ }).click();

	// full feed grouped by day
	await expect(page).toHaveURL(/\/activity$/);
	await expect(page.getByRole('heading', { name: 'Today' })).toBeVisible();
	const rows = page.locator('main li');
	await expect(rows).toHaveCount(3);
	await expect(rows.nth(0)).toContainText('Riley');
	await expect(rows.nth(0)).toContainText('marked Electric paid · $142.50');
	await expect(rows.nth(1)).toContainText('added bill Electric');
	await expect(rows.nth(2)).toContainText('added account Joint checking');
	await expect(page.getByText("That's everything.")).toBeVisible();
});
