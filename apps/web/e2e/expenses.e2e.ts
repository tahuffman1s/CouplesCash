import { expect, test } from '@playwright/test';
import { addAccount, registerWithHousehold, todayIso, txRow } from './helpers';

test('expense with deduction lowers the balance and feeds Transactions and budgets', async ({
	page
}) => {
	await registerWithHousehold(page);
	await addAccount(page, 'Everyday', '1000');

	// create through the full form
	await page.goto('/expenses/new');
	await page.getByLabel('What was it?').fill('Groceries run');
	await page.getByLabel('Amount').fill('45');
	await page.getByLabel('Date').fill(todayIso());
	await page.getByLabel('Category').selectOption({ label: 'Groceries' });
	await page.getByLabel('Account').selectOption({ label: 'Everyday' });
	await expect(page.getByLabel('Deduct from account balance')).toBeChecked();
	await page.getByRole('button', { name: 'Save expense' }).click();

	// lands on Transactions, posted under Today with its category chip
	await expect(page).toHaveURL(/\/transactions\?added=1/);
	await expect(page.getByText('Expense added')).toBeVisible();
	const day = page.locator(`[data-testid="tx-day"][data-date="${todayIso()}"]`);
	await expect(day.getByRole('heading', { name: 'Today' })).toBeVisible();
	await expect(txRow(page, 'Groceries run').getByTestId('tx-status')).toHaveText('Groceries');
	await expect(txRow(page, 'Groceries run').getByTestId('tx-amount')).toHaveText('−$45.00');
	// a typed expense only offers Edit
	await txRow(page, 'Groceries run').getByRole('button').click();
	await expect(page.getByTestId('edit-expense')).toBeVisible();
	await page.keyboard.press('Escape');

	// balance dropped
	await page.goto('/accounts');
	await expect(page.getByTestId('net-worth')).toHaveText('$955.00');

	// budgets: set Groceries to $40 → over by $5
	await page.goto('/insights');
	await expect(page.getByRole('heading', { name: 'Monthly budgets' })).toBeVisible();
	await page.getByRole('button', { name: /Unbudgeted/ }).click();
	await page
		.getByTestId('unbudgeted')
		.getByRole('button', { name: /^Groceries/ })
		.click();
	await page.getByLabel('Monthly budget for Groceries').fill('40');
	await page.getByRole('button', { name: 'Set', exact: true }).click();
	await expect(page.getByText('Groceries budget set')).toBeVisible();
	const row = page.locator('[data-testid="budget-row"][data-category="Groceries"]');
	await expect(row.getByRole('meter')).toHaveAttribute('aria-label', /over budget/);
	await expect(row).toContainText('Over by $5.00');
	await expect(page.getByTestId('budget-total')).toContainText('$45.00');

	// under budget state after raising it
	await row.getByRole('button', { name: /^Groceries/ }).click();
	await page.getByLabel('Monthly budget for Groceries').fill('100');
	await page.getByRole('button', { name: 'Set', exact: true }).click();
	await expect(row).toContainText('$55.00 left');

	// the donut's category list now includes the one-off expense
	await expect(page.getByRole('list', { name: 'Category breakdown' })).toContainText('Groceries');

	// quick add: "Already spent?" opens the expense form prefilled from the sentence
	await page.goto('/transactions');
	await page.getByTestId('quick-add').click();
	await page.getByTestId('quick-add-input').fill('coffee 4.50 today');
	await page.getByRole('link', { name: /Already spent/ }).click();
	await expect(page).toHaveURL(/\/expenses\/new\?/);
	await expect(page.getByLabel('What was it?')).toHaveValue('Coffee');
	await expect(page.getByLabel('Amount')).toHaveValue('4.50');
});
