import { expect, test } from '@playwright/test';
import { registerWithHousehold, todayIso } from './helpers';

test('create a monthly bill: it appears on the calendar and can be marked paid', async ({
	page
}) => {
	await registerWithHousehold(page);
	const today = todayIso();

	await page.goto('/bills/new');
	await page.getByRole('radiogroup', { name: 'Kind' }).getByText('Bill').click();
	await page.getByLabel('Name', { exact: true }).fill('Rent');
	await page.getByLabel('Amount').fill('1800');
	await page.getByLabel('Repeats').selectOption('monthly');
	await page.getByLabel('First due date').fill(today);
	await page.getByLabel('Category').selectOption({ label: 'Housing' });
	await page.getByRole('button', { name: 'Save', exact: true }).click();
	await expect(page).toHaveURL(/\/bills\/[0-9a-f-]+/);
	await expect(page.getByRole('heading', { name: 'Rent' })).toBeVisible();

	// list shows it with the monthly-equivalent total
	await page.goto('/bills');
	await expect(page.getByRole('link', { name: /Rent/ })).toBeVisible();
	await expect(page.getByTestId('monthly-total')).toHaveText('−$1,800.00');
	await page.getByRole('radio', { name: 'Income' }).click();
	await expect(page.getByText('No income yet.')).toBeVisible();

	// Bills › Calendar: the day has an item, open the drawer and mark it paid
	await page.getByRole('tab', { name: 'Calendar' }).click();
	await expect(page).toHaveURL(/view=calendar/);
	const day = page.locator(`[data-date="${today}"]`);
	await expect(day).toHaveAttribute('aria-label', /1 item/);
	await day.click();
	const dialog = page.getByRole('dialog');
	await expect(dialog.getByText('Rent')).toBeVisible();
	const paid = dialog.getByRole('button', { name: 'Mark Rent paid' });
	await paid.click();
	await expect(dialog.getByRole('button', { name: 'Mark Rent unpaid' })).toHaveAttribute(
		'aria-pressed',
		'true'
	);

	// persisted: reload and it is still paid
	await page.reload();
	await page.locator(`[data-date="${today}"]`).click();
	await expect(
		page.getByRole('dialog').getByRole('button', { name: 'Mark Rent unpaid' })
	).toBeVisible();

	// toggle back
	await page.getByRole('dialog').getByRole('button', { name: 'Mark Rent unpaid' }).click();
	await expect(
		page.getByRole('dialog').getByRole('button', { name: 'Mark Rent paid' })
	).toHaveAttribute('aria-pressed', 'false');

	// close the drawer, then month navigation uses the proxy and the monthly item shows up next month too
	await page.getByRole('dialog').getByRole('button', { name: 'Close' }).click();
	await expect(page.getByRole('dialog')).toHaveCount(0);
	const label = await page.getByTestId('month-label').innerText();
	await page.getByRole('button', { name: 'Next month' }).click();
	await expect(page.getByTestId('month-label')).not.toHaveText(label);
	await expect(page.locator('[data-date][aria-label*="1 item"]')).toHaveCount(1);
	expect(page.url()).toMatch(/\?view=calendar&m=\d{4}-\d{2}$/);
});
