import { expect, test } from '@playwright/test';
import {
	addAccount,
	bankSync,
	hydrated,
	linkDemoBank,
	mapChecking,
	registerWithHousehold,
	skipUnlessMockBridge,
	todayIso,
	txRow
} from './helpers';

/** v6 "Bank shows it as": several names per item, fuzzy auto-match, learned names you can forget. */
test('bank names: chips on the form, "Oakwood" auto-matches the landlord line, learned name shown', async ({
	page
}) => {
	const { email } = await registerWithHousehold(page);
	await addAccount(page, 'Everyday', '1000');

	// chip input: Enter adds, × removes
	await page.goto('/bills/new');
	await hydrated(page);
	await page.getByLabel('Name', { exact: true }).fill('Rent');
	await page.getByLabel('Amount').fill('1850');
	await page.getByLabel('First due date').fill(todayIso());
	const names = page.getByLabel('Bank shows it as');
	await names.fill('Oakwood');
	await names.press('Enter');
	await names.fill('Landlord LLC');
	await names.press('Enter');
	await expect(page.getByTestId('bank-name-chip')).toHaveText(['Oakwood', 'Landlord LLC']);
	await page.getByRole('button', { name: 'Remove Landlord LLC' }).click();
	await expect(page.getByTestId('bank-name-chip')).toHaveText(['Oakwood']);
	await page.getByRole('button', { name: 'Save', exact: true }).click();
	await expect(page).toHaveURL(/\/bills\/[0-9a-f-]+/);
	const billUrl = page.url().split('?')[0];
	await expect(page.getByTestId('bill-bank-names')).toContainText('Oakwood');

	// the landlord's ACH line arrives: name ≈ "Oakwood", amount exact → matched by itself
	await skipUnlessMockBridge(page);
	await linkDemoBank(page);
	await mapChecking(page);
	await bankSync(page, email, 'oakwood');
	await page.goto('/transactions');
	const ach = txRow(page, 'OAKWOOD PROPERTY MGMT');
	await expect(ach.getByTestId('tx-status')).toHaveText('Rent');
	await expect(ach.getByTestId('tx-auto')).toBeVisible();
	await expect(ach.getByTestId('tx-reason')).toContainText(
		'Auto-matched · name 100% · amount exact'
	);

	// the confirmed match taught the bill what the bank calls it
	await page.goto(billUrl);
	await expect(page.getByTestId('learned-name')).toContainText('OAKWOOD PROPERTY MGMT RENT');
	await expect(page.getByTestId('learned-name')).toContainText('learned');
	await page.getByRole('button', { name: 'Edit' }).click();
	await expect(page.getByTestId('bank-name-chip')).toHaveText(['Oakwood']);
	const learned = page.getByTestId('learned-chip');
	await expect(learned).toContainText('OAKWOOD PROPERTY MGMT RENT');
	await page.getByRole('button', { name: /Forget learned name/ }).click();
	await expect(page.getByText(/Forgot "OAKWOOD PROPERTY MGMT RENT"/)).toBeVisible();
	await expect(page.getByTestId('learned-chip')).toHaveCount(0);
	await page.reload();
	await expect(page.getByTestId('learned-name')).toHaveCount(0);
});
