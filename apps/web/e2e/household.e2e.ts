import { expect, test } from '@playwright/test';
import { addAccount, registerWithHousehold, todayIso } from './helpers';

test('household settings: currency, week start and pay period flow through the app', async ({
	page
}) => {
	await registerWithHousehold(page);
	await addAccount(page, 'Everyday', '1300');

	// --- one settings page; the old URL lands on its Household section
	await page.goto('/settings/household');
	await expect(page).toHaveURL(/\/settings#household$/);
	const household = page.locator('#household');
	await household.getByLabel('Currency').selectOption('EUR');
	await expect(household.getByText('€1,234.56')).toBeVisible(); // live preview
	await household.getByRole('radiogroup', { name: 'Week starts on' }).getByText('Monday').click();
	await household.getByRole('button', { name: 'Save household' }).click();
	await expect(page.getByText('Saved', { exact: true })).toBeVisible();

	// the week renders euros
	await page.goto('/week');
	await expect(page.getByTestId('stat-spendable')).toHaveText('€1,300.00');
	await expect(page.getByTestId('left-at-end')).toHaveText('€1,300.00');
	await expect(page.locator('main')).not.toContainText('$');
	await expect(page.getByTestId('next-payday')).toHaveCount(0);

	// Bills › Calendar starts on Monday
	await page.goto('/bills?view=calendar');
	await expect(page.getByTestId('weekday-headers').locator('span').first()).toHaveText('M');

	// --- pay period: preview updates live, then the week shows "Next payday"
	await page.goto('/settings#household');
	await expect(page.getByTestId('payday-preview')).toContainText('Pick a payday');
	await page.getByRole('radiogroup', { name: 'Pay period' }).getByText('Every two weeks').click();
	await page.getByLabel('A recent payday').fill(todayIso());
	await expect(page.getByTestId('payday-preview')).toContainText(/Next payday:.*in 14 days/);
	await page.getByRole('button', { name: 'Save pay period' }).click();
	await expect(page.getByText('Saved', { exact: true })).toBeVisible();

	await page.goto('/week');
	await expect(page.getByTestId('next-payday')).toContainText('Next payday');

	// the old pay-period page folds into the week
	await page.goto('/payperiod');
	await expect(page).toHaveURL(/\/week$/);

	// couple page explains re-inviting after a leave
	await page.goto('/couple');
	await expect(page.getByTestId('invite-new-partner')).toContainText('fresh Couple Code');
});
