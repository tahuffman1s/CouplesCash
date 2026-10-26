import { expect, test } from '@playwright/test';
import {
	addAccount,
	linkDemoBank,
	mapChecking,
	registerWithHousehold,
	sheet,
	skipUnlessMockBridge
} from './helpers';

/** v6 Money: net worth, accounts with "N pending · $X", pending grouped by account, goals. */
test('pending is grouped by account in Money and pinned on Transactions', async ({ page }) => {
	await registerWithHousehold(page);
	await addAccount(page, 'Everyday', '1000');
	await skipUnlessMockBridge(page);
	await linkDemoBank(page, 'demo-v5');
	await mapChecking(page);

	await page.goto('/pending');
	await expect(page).toHaveURL(/\/accounts#pending$/);
	await expect(page.getByTestId('net-worth')).toHaveText('$2,500.00');
	await expect(page.getByTestId('account-pending')).toHaveText('2 pending · −$41.00');
	await expect(page.getByTestId('pending-total')).toHaveText('2 · −$41.00');
	const group = page.locator('[data-testid="pending-group"][data-account="Everyday"]');
	await expect(group.getByTestId('pending-subtotal')).toHaveText('−$41.00');
	await expect(group.getByTestId('pending-balance')).toHaveText(
		/2 pending · balance \$2,500\.00 → available \$2,459\.00/
	);
	await expect(group.getByTestId('tx-row')).toHaveCount(2);

	// a pending row opens the transaction sheet
	await group.getByTestId('tx-row').filter({ hasText: 'THE DINER' }).getByRole('button').click();
	await expect(sheet(page).getByTestId('sheet-pending')).toBeVisible();
	await sheet(page).getByRole('button', { name: 'Fun', exact: true }).click();
	await expect(page.getByText('Expense added in Fun')).toBeVisible();

	// handled pending rows are pinned on Transactions with their account and a Pending badge
	await page.goto('/transactions');
	const pinned = page.getByTestId('pending-section').getByTestId('tx-row');
	await expect(pinned.filter({ hasText: 'THE DINER' })).toContainText('Everyday');
	await expect(pinned.filter({ hasText: 'THE DINER' }).getByTestId('tx-pending')).toHaveText(
		'Pending'
	);
	// the week counts pending money beside what is spendable
	await page.goto('/week');
	await expect(page.getByTestId('stat-pending')).toHaveText('−$41.00');
	await expect(page.getByTestId('spendable-label')).toHaveText('Available now');
});
