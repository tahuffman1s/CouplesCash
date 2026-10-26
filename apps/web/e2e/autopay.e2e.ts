import { expect, test } from '@playwright/test';
import { MOCK_API, registerWithHousehold, todayIso } from './helpers';

test('autopay: the scheduler marks the bill, the bill shows Auto-paid with Undo', async ({
	page
}) => {
	await registerWithHousehold(page);
	const today = todayIso();

	await page.goto('/bills/new');
	await page.getByLabel('Name', { exact: true }).fill('Rent');
	await page.getByLabel('Amount').fill('1850');
	await page.getByLabel('First due date').fill(today);
	await page.getByLabel('Paid automatically (auto-pay)').check();
	await page.getByRole('button', { name: 'Save', exact: true }).click();
	await expect(page).toHaveURL(/\/bills\/[0-9a-f-]+/);
	const billUrl = page.url().split('?')[0];

	// the Bills list says Autopay; the occurrence reads Planned until the job runs
	await page.goto('/bills');
	await expect(page.getByTestId('bill-row').filter({ hasText: 'Rent' })).toContainText('Autopay');

	// simulate the background job (against a real API the mock marks nothing → skip)
	const res = await page.request.post(`${MOCK_API}/__mock/autopay?date=${today}`);
	expect(res.ok()).toBeTruthy();
	const { marked } = (await res.json()) as { marked: number };
	test.skip(marked === 0, 'scheduler fixture only exists in the mock API');

	await page.goto(billUrl);
	const row = page.getByTestId('occurrence-row').filter({ hasText: 'Rent' }).first();
	await expect(row).toContainText('Auto-paid');
	await row.getByRole('button', { name: 'Undo auto-pay for Rent' }).click();
	await expect(row.getByRole('button', { name: 'Mark Rent paid' })).toBeVisible();
	await page.reload();
	await expect(
		page.getByTestId('occurrence-row').first().getByRole('button', { name: 'Mark Rent paid' })
	).toBeVisible();
});
