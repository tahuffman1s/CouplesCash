import { expect, test } from '@playwright/test';
import { addAccount, registerWithHousehold, reviewRow } from './helpers';

const CSV = [
	'Date,Description,Debit,Credit',
	'10/03/2026,KROGER #123,12.49,',
	'10/03/2026,KROGER #123,12.49,', // exact duplicate → counted by the API, not imported twice
	'10/05/2026,ACME PAYROLL,,"3,200.00"',
	'10/21/2026,SHELL OIL 4411,45.10,',
	',Opening balance,,' // no date → skipped client-side
].join('\n');

test('CSV import: auto-detected debit/credit mapping and MM/DD/YYYY dates → To review', async ({
	page
}) => {
	await registerWithHousehold(page);
	await addAccount(page, 'Everyday', '1000');

	await page.goto('/imports/csv');
	await expect(page.getByRole('heading', { name: '1 · Choose the file' })).toBeVisible();
	await page.getByLabel('Account').selectOption({ label: 'Everyday · Bank' });
	await page
		.getByLabel('CSV file')
		.setInputFiles({ name: 'statement.csv', mimeType: 'text/csv', buffer: Buffer.from(CSV) });

	// step 2: everything detected from the header row and the sample values
	await expect(page.getByRole('heading', { name: '2 · Match the columns' })).toBeVisible();
	await expect(page.getByRole('radio', { name: 'Debit + credit' })).toHaveAttribute(
		'aria-checked',
		'true'
	);
	await expect(page.getByLabel('Date', { exact: true })).toHaveValue('0');
	await expect(page.getByLabel('Description')).toHaveValue('1');
	await expect(page.getByLabel('Debit (money out)')).toHaveValue('2');
	await expect(page.getByLabel('Credit (money in)')).toHaveValue('3');
	await expect(page.getByLabel('Date format')).toHaveValue('mdy');
	await expect(page.getByTestId('date-sample')).toContainText('→ 2026-10-03');
	await expect(page.getByTestId('preview-count')).toHaveText('4 ready · 1 skipped');
	await expect(page.locator('table tbody tr').first()).toContainText('−$12.49');
	await expect(page.locator('table tbody tr').nth(2)).toContainText('+$3,200.00');

	// a wrong date format shows up in the sample and empties the preview
	await page.getByLabel('Date format').selectOption('iso');
	await expect(page.getByTestId('date-sample')).toContainText('not recognised');
	await expect(page.getByTestId('preview-count')).toContainText('0 ready');
	await page.getByLabel('Date format').selectOption('mdy');

	// import
	await page.getByRole('button', { name: /Import 4 rows into Everyday/ }).click();
	const result = page.getByTestId('import-result');
	await expect(result).toBeVisible();
	await expect(page.getByTestId('result-imported')).toHaveText('3');
	await expect(page.getByTestId('result-duplicates')).toHaveText('1');
	await expect(page.getByTestId('result-skipped')).toHaveText('1');
	await expect(result).toContainText('3 transactions now wait in To review');

	await page.getByRole('link', { name: 'Review them' }).click();
	await expect(page).toHaveURL(/\/transactions$/);
	await expect(page.getByTestId('review-row')).toHaveCount(3);
	await expect(page.getByTestId('review-badge')).toHaveText('3');
	const row = (text: string) => reviewRow(page, text);
	await expect(row('KROGER #123')).toContainText('CSV');
	await expect(row('KROGER #123').getByTestId('review-primary')).toHaveText('Groceries');
	await expect(row('SHELL OIL 4411').getByTestId('review-primary')).toHaveText('Transportation');
	// money in is never an expense: the payroll row asks
	await expect(row('ACME PAYROLL').getByTestId('review-primary')).toHaveText('Choose…');
	await expect(row('ACME PAYROLL').getByTestId('review-account')).toContainText('Everyday');

	// the recent-imports list remembers it; the balance was not touched
	await page.goto('/imports/csv');
	await expect(page.getByText('statement.csv')).toBeVisible();
	await expect(page.getByText('3 new')).toBeVisible();
	await page.goto('/accounts');
	await expect(page.getByTestId('net-worth')).toHaveText('$1,000.00');
});
