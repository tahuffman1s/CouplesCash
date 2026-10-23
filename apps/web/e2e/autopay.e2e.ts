import { expect, test } from '@playwright/test';
import { MOCK_API, registerWithHousehold, todayIso } from './helpers';

test('autopay: scheduler marks the bill, UI shows Auto-paid with Undo', async ({ page }) => {
	await registerWithHousehold(page);
	const today = todayIso();

	await page.goto('/bills/new');
	await page.getByLabel('Name').fill('Rent');
	await page.getByLabel('Amount').fill('1850');
	await page.getByLabel('First due date').fill(today);
	await page.getByLabel('Paid automatically (auto-pay)').check();
	await page.getByRole('button', { name: 'Save', exact: true }).click();
	await expect(page).toHaveURL(/\/bills\/[0-9a-f-]+/);

	// unpaid autopay rows carry an Autopay badge
	await page.goto('/');
	await expect(page.getByText('Autopay', { exact: true })).toBeVisible();
	await expect(page.getByTestId('autopay-banner')).toHaveCount(0);

	// simulate the background job
	// (when the web is pointed at the real API the mock knows nothing about this
	// household and marks nothing — the scheduler itself is covered by the API smoke test)
	const res = await page.request.post(`${MOCK_API}/__mock/autopay?date=${today}`);
	expect(res.ok()).toBeTruthy();
	const { marked } = (await res.json()) as { marked: number };
	test.skip(marked === 0, 'scheduler fixture only exists in the mock API');

	// dashboard banner with Undo
	await page.reload();
	const banner = page.getByTestId('autopay-banner');
	await expect(banner).toContainText('Rent');
	await expect(banner).toContainText('auto-paid');
	await expect(banner).toContainText('$1,850.00');

	// calendar: Auto-paid + Undo → unpaid again
	await page.goto('/calendar');
	await expect(page.getByTestId('autopay-note')).toBeVisible();
	await page.locator(`[data-date="${today}"]`).click();
	const dialog = page.getByRole('dialog');
	await expect(dialog.getByText('Auto-paid')).toBeVisible();
	await dialog.getByRole('button', { name: 'Undo auto-pay for Rent' }).click();
	await expect(dialog.getByRole('button', { name: 'Mark Rent paid' })).toHaveAttribute(
		'aria-pressed',
		'false'
	);
	await page.reload();
	await page.locator(`[data-date="${today}"]`).click();
	await expect(
		page.getByRole('dialog').getByRole('button', { name: 'Mark Rent paid' })
	).toBeVisible();

	// the system event shows up in the feed as "Autopay"
	await page.goto('/activity');
	await expect(page.locator('main li').filter({ hasText: 'auto-paid Rent' })).toContainText(
		'Autopay'
	);
});
