import { expect, test } from '@playwright/test';
import { registerWithHousehold } from './helpers';

test('manual goal: create, contribute, ring percent, accounts section', async ({ page }) => {
	await registerWithHousehold(page);
	await page.goto('/goals');
	await expect(page.getByText('No goals yet')).toBeVisible();
	await page.getByRole('link', { name: 'Create a goal' }).click();
	await expect(page).toHaveURL(/\/goals\/new$/);

	await page.getByLabel('Goal name').fill('Japan trip');
	await page.getByLabel('Target amount').fill('1000');
	await page.getByLabel('Where is the money?').selectOption('');
	await page.getByRole('button', { name: 'Create goal' }).click();
	await expect(page).toHaveURL(/\/goals\/[0-9a-f-]+/);
	await expect(page.getByTestId('goal-percent')).toHaveText('0%');
	await expect(page.getByText('No history yet')).toBeVisible();

	await page.getByRole('button', { name: 'Contribute' }).click();
	await page.getByTestId('contribute-amount').fill('250');
	await page.getByRole('button', { name: 'Add to goal' }).click();
	await expect(page.getByText('$250.00 added to Japan trip')).toBeVisible();
	await expect(page.getByTestId('goal-percent')).toHaveText('25%');
	await expect(page.getByTestId('goal-current')).toHaveText('$250');
	await expect(page.getByRole('meter')).toHaveAttribute('aria-valuenow', '25');
	await expect(page.getByRole('img', { name: /Goal history/ })).toBeVisible();

	// listed on the accounts page and /goals
	await page.goto('/accounts');
	const card = page.getByTestId('goals-section').getByTestId('goal-card');
	await expect(card).toHaveCount(1);
	await expect(card.getByTestId('goal-percent')).toHaveText('25%');
	await page.goto('/goals');
	await expect(page.getByTestId('goal-card')).toContainText('Japan trip');
});
