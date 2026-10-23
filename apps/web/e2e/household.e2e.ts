import { expect, test } from '@playwright/test';
import { addAccount, registerWithHousehold, todayIso } from './helpers';

test('household settings: currency, week start and pay period flow through the app', async ({
	page
}) => {
	await registerWithHousehold(page);
	await addAccount(page, 'Everyday', '1300');

	// --- general: EUR + Monday
	await page.goto('/settings/household');
	await page.getByLabel('Currency').selectOption('EUR');
	await expect(page.getByText('€1,234.56')).toBeVisible(); // live preview
	await page.getByRole('radiogroup', { name: 'Week starts on' }).getByText('Monday').click();
	await page.getByRole('button', { name: 'Save', exact: true }).click();
	await expect(page.getByText('Household settings saved')).toBeVisible();

	// dashboard renders euros everywhere
	await page.goto('/');
	await expect(page.getByTestId('hero-net-worth')).toHaveText('€1,300');
	const monthCard = page.locator('section[aria-labelledby="month"]');
	await expect(monthCard).toContainText('€0.00');
	await expect(monthCard).not.toContainText('$');
	await expect(page.locator('section[aria-labelledby="networth"]')).not.toContainText('$');
	// the payday card is replaced by a one-line setup CTA until a pay period exists
	await expect(page.getByTestId('payday-setup')).toBeVisible();

	// calendar grid starts on Monday
	await page.goto('/calendar');
	await expect(page.getByTestId('weekday-headers').locator('span').first()).toHaveText('M');

	// --- pay period: preview updates live, then the dashboard card appears
	await page.goto('/settings/household');
	await expect(page.getByTestId('payday-preview')).toContainText('Pick a payday');
	await page.getByRole('radiogroup', { name: 'Pay period' }).getByText('Every two weeks').click();
	await page.getByLabel('A recent payday').fill(todayIso());
	await expect(page.getByTestId('payday-preview')).toContainText(/Next payday:.*in 14 days/);
	await page.getByRole('button', { name: 'Save pay period' }).click();
	await expect(page.getByText('Household settings saved')).toBeVisible();

	await page.goto('/');
	const card = page.getByTestId('payday-card');
	await expect(card).toBeVisible();
	await expect(card).toContainText('14 days left');
	await expect(page.getByTestId('spendable')).toHaveText('€1,300');

	// /payperiod shows the same header and period navigation works
	await page.getByRole('link', { name: 'Details' }).click();
	await expect(page).toHaveURL(/\/payperiod$/);
	await expect(page.getByTestId('period-label')).toContainText('now');
	await page.getByRole('link', { name: 'Next period' }).click();
	await expect(page).toHaveURL(/\/payperiod\?date=\d{4}-\d{2}-\d{2}$/);
	await expect(page.getByTestId('period-label')).not.toContainText('now');

	// couple page explains re-inviting after a leave
	await page.goto('/couple');
	await expect(page.getByTestId('invite-new-partner')).toContainText('fresh Couple Code');
});
