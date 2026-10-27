import { expect, test } from '@playwright/test';
import {
	addTransaction,
	bankSync,
	hydrated,
	registerWithHousehold,
	setupHousehold,
	skipUnlessMockBridge,
	todayIso,
	usd,
	weekdayOf
} from './helpers';

/**
 * "+" opens on Transaction: a charge the bank hasn't shown yet. It comes off "available now" right
 * away, is listed with the pending rows, and the bank's own copy takes its place when it arrives.
 * The mock's household bank: US Bank available $2,345.67, 3 pending card holds (−$62.40).
 */
test('a transaction added by hand counts until the bank shows it, then the bank copy replaces it', async ({
	page
}) => {
	const { email } = await registerWithHousehold(page, 'Travis');
	await skipUnlessMockBridge(page);
	// payday today: the bank's paycheck arrives with the payday phase
	await setupHousehold(page, { payday: weekdayOf(todayIso()) });
	await expect(page.getByTestId('plan-amount-available')).toHaveText('$2,345.67');

	// the sheet starts on Transaction, money out, the bills account, today
	await hydrated(page);
	await page.getByTestId('quick-add').click();
	await expect(page.getByTestId('quick-tab-transaction')).toHaveAttribute('aria-selected', 'true');
	await expect(page.getByTestId('add-tx-account')).toHaveValue(
		(await page
			.getByTestId('add-tx-account')
			.locator('option', { hasText: 'U.S. Bank' })
			.getAttribute('value')) ?? ''
	);
	await expect(page.getByTestId('add-tx-date')).toHaveValue(todayIso());
	await page.keyboard.press('Escape');
	await expect(page.getByTestId('add-tx-form')).toHaveCount(0);

	// a coffee the bank hasn't shown: available drops by it, and the pending list says who added it
	await addTransaction(page, { amount: '-4.25', description: 'Coffee' });
	await expect(page.getByText('Coffee added · $4.25')).toBeVisible();
	await expect(page.getByTestId('plan-amount-available')).toHaveText(usd(234567 - 425));
	await expect(page.getByTestId('pending-subline')).toHaveText(
		'after 3 pending · −$62.40 · 1 you added −$4.25'
	);
	await page.getByTestId('plan-row-available').locator('summary').click();
	await expect(page.getByTestId('pending-added')).toHaveCount(1);

	// the bank already shows a Starbucks −$6.50 today: it asks first, "Add anyway" stores it
	await addTransaction(page, { amount: '-6.50', description: 'Starbucks' });
	await expect(page.getByTestId('add-tx-error')).toContainText('already shows STARBUCKS 4471');
	await page.getByTestId('add-tx-force').click();
	await expect(page.getByText('Starbucks added · $6.50')).toBeVisible();
	await expect(page.getByTestId('plan-amount-available')).toHaveText(usd(234567 - 425 - 650));

	// money in: the paycheck, before the bank shows it — the bank's deposit then replaces it
	await addTransaction(page, { amount: '2450', description: 'Paycheck' });
	await expect(page.getByTestId('plan-amount-available')).toHaveText(
		usd(234567 - 425 - 650 + 245000)
	);
	await bankSync(page, email, 'payday');
	await page.goto('/transactions');
	const pending = page.getByTestId('pending-section');
	await expect(pending.getByTestId('tx-row').filter({ hasText: 'Paycheck' })).toHaveCount(0);
	await expect(pending.getByTestId('tx-row').filter({ hasText: 'Coffee' })).toContainText(
		'Added by you'
	);

	// delete the forced Starbucks from its sheet
	await pending
		.getByTestId('tx-row')
		.filter({ hasText: 'Starbucks' })
		.filter({ hasText: 'Added by you' })
		.click();
	await page.getByTestId('delete-added').click();
	await expect(page.getByText('Removed Starbucks')).toBeVisible();
	await expect(
		pending
			.getByTestId('tx-row')
			.filter({ hasText: 'Starbucks' })
			.filter({ hasText: 'Added by you' })
	).toHaveCount(0);
});

test('the bill tab still adds bills and one-offs from a sentence', async ({ page }) => {
	await registerWithHousehold(page);
	await page.goto('/bills');
	await hydrated(page);
	await page.getByTestId('quick-add').click();
	await page.getByTestId('quick-tab-bill').click();
	await page.getByTestId('quick-add-input').fill('netflix 15.49 on the 7th');
	await expect(page.getByTestId('quick-add-preview')).toBeVisible();
	await page.getByTestId('quick-add-submit').click();
	await expect(page.getByTestId('bill-row').filter({ hasText: /netflix/i })).toBeVisible();
});
