import { expect, test, type Page } from '@playwright/test';
import { hydrated, quickAdd, registerWithHousehold, shiftDays, todayIso } from './helpers';

/** v6: one text field, a live preview with tappable chips, Add → the right item. */

/** The next `weekday` (0 = Sunday) on or after today. */
function nextWeekday(wd: number): string {
	const t = todayIso();
	const [y, m, d] = t.split('-').map(Number);
	const cur = new Date(y, m - 1, d).getDay();
	return shiftDays(t, (wd - cur + 7) % 7);
}
const monthDay = (iso: string) =>
	new Date(`${iso}T00:00`).toLocaleDateString('en-US', { month: 'short', day: 'numeric' });

async function openBill(page: Page, name: string) {
	await page.goto('/bills');
	await page.getByRole('radio', { name: 'All' }).click();
	await page
		.getByRole('link', { name: new RegExp(name) })
		.first()
		.click();
	await expect(page).toHaveURL(/\/bills\/[0-9a-f-]+/);
}

test('"netflix 15.49 on the 7th" → a monthly subscription on day 7, named for the bank', async ({
	page
}) => {
	await registerWithHousehold(page);
	await page.goto('/bills');
	await hydrated(page);
	await page.getByTestId('quick-add').click();
	const form = page.getByTestId('quick-add-form');
	await expect(form.getByTestId('quick-add-submit')).toBeDisabled();
	// examples fill the field
	await form.getByRole('button', { name: 'netflix 15.49 on the 7th' }).click();
	await expect(form.getByTestId('preview-name')).toHaveText('Netflix');
	await expect(form.getByTestId('preview-amount')).toHaveText('−$15.49');
	await expect(form.getByTestId('chip-kind')).toHaveText('Subscription');
	await expect(form.getByTestId('chip-cadence')).toHaveText('Every month');
	await expect(form.getByTestId('chip-category')).toContainText('Subscriptions');
	await expect(form.getByTestId('preview-schedule')).toContainText('every month · next');
	await form.getByTestId('quick-add-submit').click();
	await expect(page.getByText(/Netflix added · every month · next/)).toBeVisible();

	await page.getByRole('radio', { name: 'Subscriptions' }).click();
	const row = page.getByTestId('bill-list').getByRole('link', { name: /Netflix/ });
	await expect(row).toContainText('−$15.49');
	await row.click();
	await expect(page.getByRole('heading', { name: 'Netflix' })).toBeVisible();
	await expect(page.getByText('Subscription · Monthly')).toBeVisible();
	await expect(page.getByTestId('bill-bank-names')).toContainText('Netflix');
	// next due is the 7th
	await expect(page.locator('dd').first()).toHaveText(/ 7$|^\w+ 7, \d{4}$/);
});

test('"oil change 60 friday" → a one-off this Friday', async ({ page }) => {
	await registerWithHousehold(page);
	await page.goto('/week');
	await quickAdd(page, 'oil change 60 friday');
	await expect(page.getByText(/Oil change added · one time/)).toBeVisible();
	await page.goto('/bills?tab=once');
	await page.getByRole('radio', { name: 'One-offs' }).click();
	const row = page.getByTestId('one-offs').getByRole('link', { name: /Oil change/ });
	await expect(row).toContainText(monthDay(nextWeekday(5)));
	await expect(row).toContainText('−$60.00');
	await row.click();
	await expect(page.getByText('Bill · One time')).toBeVisible();
});

test('"paycheck 3200 every other friday" → income every two weeks', async ({ page }) => {
	await registerWithHousehold(page);
	await page.goto('/transactions');
	await quickAdd(page, 'paycheck 3200 every other friday');
	await expect(page.getByText(/Paycheck added · every 2 weeks · next/)).toBeVisible();
	await page.goto('/bills');
	await page.getByRole('radio', { name: 'Income' }).click();
	await expect(page.getByTestId('monthly-total')).toHaveText('+$6,933.33');
	await openBill(page, 'Paycheck');
	await expect(page.getByText('Income · Every 2 weeks')).toBeVisible();
	await expect(page.locator('dd').first()).toHaveText(monthDay(nextWeekday(5)));
});

test('chips correct what the sentence got wrong; every 6 months is two yearly items', async ({
	page
}) => {
	await registerWithHousehold(page);
	await page.goto('/bills');
	await hydrated(page);
	await page.getByTestId('quick-add').click();
	const form = page.getByTestId('quick-add-form');
	await form.getByTestId('quick-add-input').fill('gym 45 weekly');
	await expect(form.getByTestId('chip-kind')).toHaveText('Bill');
	await expect(form.getByTestId('chip-category')).toContainText('Health');
	// kind: tap cycles Bill → Subscription
	await form.getByTestId('chip-kind').click();
	await expect(form.getByTestId('chip-kind')).toHaveText('Subscription');
	// frequency: tap opens a picker
	await form.getByTestId('chip-cadence').click();
	await form.getByLabel('How often').selectOption('monthly');
	await expect(form.getByTestId('chip-cadence')).toHaveText('Every month');
	// amount: tap to edit, mark as varying
	await form.getByTestId('chip-amount').click();
	await form.getByLabel('Amount').fill('49');
	await form.getByLabel('Varies').check();
	await expect(form.getByTestId('chip-amount')).toHaveText('~$49.00');
	await form.getByTestId('quick-add-submit').click();
	await expect(page.getByText(/Gym added · every month/)).toBeVisible();
	await openBill(page, 'Gym');
	await expect(page.getByText('Subscription · Monthly')).toBeVisible();
	await expect(page.getByText('$49.00').first()).toBeVisible();
	await expect(page.getByText('Varies')).toBeVisible();

	// a cadence the API has no word for is saved as two items
	await page.goto('/bills');
	await quickAdd(page, 'car insurance 216 every 6 months');
	await expect(page.getByText(/Car insurance added · 2 items · every 6 months/)).toBeVisible();
	await expect(
		page.getByTestId('bill-list').getByRole('link', { name: /Car insurance/ })
	).toHaveCount(2);

	// "More options" carries the sentence into the full form
	await hydrated(page);
	await page.getByTestId('quick-add').click();
	await page.getByTestId('quick-add-input').fill('water bill ~60 on the 20th');
	await page.getByTestId('quick-more').click();
	await expect(page).toHaveURL(/\/bills\/new\?/);
	await expect(page.getByLabel('Name', { exact: true })).toHaveValue('Water bill');
	await expect(page.getByLabel('Amount')).toHaveValue('60.00');
	await expect(page.getByTestId('bank-name-chip')).toHaveText(['Water bill']);
});
