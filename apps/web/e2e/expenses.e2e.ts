import { expect, test } from '@playwright/test';
import { addAccount, registerWithHousehold, todayIso } from './helpers';

test('expense with deduction lowers the balance and feeds Bills › Expenses and budgets', async ({
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

	// lands on Bills › Expenses, grouped by day with a total
	await expect(page).toHaveURL(/\/bills\?tab=expenses/);
	await expect(page.getByRole('radio', { name: 'Expenses' })).toHaveAttribute(
		'aria-checked',
		'true'
	);
	await expect(page.getByRole('heading', { name: 'Today' })).toBeVisible();
	await expect(page.getByRole('link', { name: /Groceries run/ })).toBeVisible();
	await expect(page.getByTestId('expenses-total')).toHaveText('$45.00');

	// month nav through the proxy: previous month is empty
	await page.getByRole('button', { name: 'Previous month' }).click();
	await expect(page.getByTestId('expenses-total')).toHaveText('$0.00');
	await expect(page.getByText('No expenses this month')).toBeVisible();

	// balance dropped
	await page.goto('/accounts');
	await expect(page.getByTestId('net-worth')).toHaveText('$955.00');

	// budgets: set Groceries to $40 → over by $5
	await page.goto('/insights');
	await expect(page.getByRole('heading', { name: 'Budgets' })).toBeVisible();
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

	// quick-add from the dashboard: bottom sheet → inline expense form → toast
	await page.goto('/');
	await page.getByTestId('quick-add').click();
	await page.getByRole('button', { name: /Expense/ }).click();
	await page.getByLabel('What was it?').fill('Coffee');
	await page.getByLabel('Amount').fill('4.50');
	await page.getByRole('button', { name: 'Add expense' }).click();
	await expect(page.getByText('Coffee added')).toBeVisible();
	await expect(page.getByRole('dialog')).toHaveCount(0);
	await expect(page.getByTestId('recent-activity')).toContainText('Coffee');
});
