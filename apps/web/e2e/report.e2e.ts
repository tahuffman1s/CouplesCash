import { expect, test } from '@playwright/test';
import { addAccount, registerWithHousehold, todayIso } from './helpers';

test('month report renders the month data with Share and Print', async ({ page }) => {
	await registerWithHousehold(page);
	await addAccount(page, 'Everyday', '2500');
	await page.goto('/expenses/new');
	await page.getByLabel('What was it?').fill('New tyres');
	await page.getByLabel('Amount').fill('320');
	await page.getByLabel('Date').fill(todayIso());
	await page.getByLabel('Category').selectOption({ label: 'Transportation' });
	await page.getByLabel('Account').selectOption({ label: 'Everyday' });
	await page.getByRole('button', { name: 'Save expense' }).click();
	await expect(page).toHaveURL(/\/transactions\?added=1/);

	await page.goto('/insights');
	await expect(page.getByTestId('report-link')).toHaveText(/Share this month/);
	await page.getByTestId('report-link').click();
	await expect(page).toHaveURL(/\/insights\/report\?m=\d{4}-\d{2}$/);

	const report = page.getByTestId('month-report');
	await expect(report).toBeVisible();
	await expect(report).toContainText('Test House');
	await expect(report).toContainText('$2,180'); // 2500 − 320 net worth
	await expect(report).toContainText('First month on record');
	await expect(report).toContainText('Biggest expenses');
	await expect(report).toContainText('New tyres');
	await expect(report).toContainText('Transportation');
	await expect(report).toContainText(/Generated with CouplesCash/);
	await expect(page.getByTestId('share-report')).toBeEnabled();
	await expect(page.getByRole('button', { name: 'Print' })).toBeVisible();

	// month navigation
	await page.getByRole('link', { name: 'Previous month' }).click();
	await expect(page.getByTestId('month-report')).not.toContainText('New tyres');
});
