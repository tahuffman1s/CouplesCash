import { expect, test } from '@playwright/test';
import { addBill, hydrated, registerWithHousehold, todayIso } from './helpers';

/** v7 Settings → Household → Start over: erases the household's data (for both of them) after typing RESET. */
test('start over erases the household data only after typing RESET', async ({ page }) => {
	await registerWithHousehold(page, 'Travis');
	await addBill(page, { name: 'Rent', amount: '1850', firstDue: todayIso() });
	await page.goto('/bills');
	await expect(page.getByTestId('bill-row')).toHaveCount(1);

	await page.goto('/settings#household');
	await hydrated(page);
	const startOver = page.getByTestId('start-over');
	await startOver.locator('summary').click();
	await expect(startOver).toContainText('for both of you');

	// the wrong word does nothing
	await page.getByTestId('reset-confirm').fill('yes');
	await page.getByTestId('reset-submit').click();
	await expect(startOver.getByRole('alert')).toHaveText('Type RESET to confirm.');
	await page.goto('/bills');
	await expect(page.getByTestId('bill-row')).toHaveCount(1);

	// RESET: everything goes, setup starts again, still signed in
	await page.goto('/settings#household');
	await hydrated(page);
	await startOver.locator('summary').click();
	await page.getByTestId('reset-confirm').fill('reset');
	await page.getByTestId('reset-submit').click();
	await expect(page).toHaveURL(/\/week$/);
	await expect(page.getByTestId('setup-overview')).toBeVisible();
	await page.goto('/bills');
	await expect(page.getByTestId('bill-row')).toHaveCount(0);
	await expect(page.getByRole('heading', { name: 'No bills yet' })).toBeVisible();
});
