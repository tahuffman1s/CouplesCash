import { expect, test, type Page } from '@playwright/test';
import {
	addAccount,
	addBill,
	bankSync,
	linkDemoBank,
	mapChecking,
	registerWithHousehold,
	reviewRow,
	sheet,
	shiftDays,
	skipUnlessMockBridge,
	todayIso,
	txRow
} from './helpers';

/**
 * v5 flows on the v6 screens: pending lifecycle, transfers, per-account transactions and the shared
 * transaction sheet. The `demo-v5` bank (checking + savings with a pending diner bill, a fuel hold,
 * a transfer pair, a one-sided brokerage transfer and a gym charge) and the `?phase=` Bridge edits
 * only exist in the mock API, so every test skips itself against a real API; the real paths are
 * covered by apps/api/scripts/smoke.sh with the fake SimpleFIN server.
 */
async function setup(page: Page, opts: { savings?: boolean } = {}) {
	const creds = await registerWithHousehold(page);
	await addAccount(page, 'Everyday', '1000');
	const accountUrl = page.url().split('?')[0];
	await skipUnlessMockBridge(page);
	await linkDemoBank(page, 'demo-v5');
	await mapChecking(page);
	if (opts.savings) {
		await page
			.getByLabel('Mapped account for Demo Bank · Way2Save Savings')
			.selectOption('__create');
		await page.getByLabel('New account name').fill('Savings');
		await page.getByRole('button', { name: 'Create & map' }).click();
		await expect(page.getByText('Way2Save Savings now updates Savings')).toBeVisible();
	}
	return { ...creds, accountUrl };
}

test('pending lifecycle: a tipped bill posts in place, a dropped hold disappears', async ({
	page
}) => {
	const { email, accountUrl } = await setup(page);

	// the authorization is pending at −$40.00; confirm it as a Fun expense from the sheet
	await page.goto('/transactions');
	const diner = reviewRow(page, 'THE DINER');
	await expect(diner).toContainText('Pending');
	await expect(diner.getByTestId('review-amount')).toHaveText('−$40.00');
	await diner.getByTestId('review-else').click();
	await expect(sheet(page).getByTestId('sheet-pending')).toBeVisible();
	await expect(sheet(page).getByTestId('sheet-state')).toHaveText('To review');
	await expect(sheet(page)).toContainText('Everyday');
	await sheet(page).getByRole('button', { name: 'Fun', exact: true }).click();
	await expect(page.getByText('Expense added in Fun')).toBeVisible();
	// handled but still in flight: pinned under Pending with its account
	const pending = page.getByTestId('pending-section');
	await expect(txRow(page, 'THE DINER').getByTestId('tx-status')).toHaveText('Fun');
	await expect(pending.getByTestId('tx-row').filter({ hasText: 'THE DINER' })).toContainText(
		'Everyday'
	);

	// the account shows its pending rows (diner $40 + fuel hold $1)
	await page.goto(`${accountUrl}?tab=transactions`);
	await expect(page.getByTestId('tx-available')).toContainText('$41.00 pending');

	// the bank posts it with the tip under a new id → the same row, now −$48.00, still Fun
	await bankSync(page, email, 'post');
	await page.goto('/transactions');
	await expect(txRow(page, 'THE DINER')).toHaveCount(1);
	await expect(txRow(page, 'THE DINER').getByTestId('tx-amount')).toHaveText('−$48.00');
	await expect(txRow(page, 'THE DINER').getByTestId('tx-pending')).toHaveCount(0);
	await expect(txRow(page, 'THE DINER').getByTestId('tx-status')).toHaveText('Fun');
	await expect(reviewRow(page, 'THE DINER')).toHaveCount(0);

	// the fuel hold is on the account until the bank drops it
	const holdMonth = shiftDays(todayIso(), -6).slice(0, 7);
	await page.goto(`${accountUrl}?tab=transactions&month=${holdMonth}`);
	await expect(page.getByTestId('tx-row').filter({ hasText: 'SHELL OIL' })).toHaveCount(1);
	await bankSync(page, email, 'drop');
	await page.reload();
	await expect(page.getByTestId('tx-month')).toBeVisible();
	await expect(page.getByTestId('tx-row').filter({ hasText: 'SHELL OIL' })).toHaveCount(0);
	await page.goto('/transactions');
	await expect(reviewRow(page, 'SHELL OIL')).toHaveCount(0);
	await page.goto(`${accountUrl}?tab=transactions`);
	await expect(page.getByTestId('tx-balance')).toBeVisible();
	await expect(page.getByText(/\$41\.00 pending/)).toHaveCount(0);
});

test('transfers: auto-ignored pair, one-sided + learned payee', async ({ page }) => {
	const { email } = await setup(page, { savings: true });

	// both sides of the pair were ignored automatically: posted rows with the other side named
	await page.goto('/transactions');
	const out = txRow(page, 'ONLINE TRANSFER TO SAV REF 123');
	await expect(out.getByTestId('tx-status')).toHaveText('Transfer → Savings');
	await expect(out.getByTestId('tx-auto')).toBeVisible();
	await expect(txRow(page, 'ONLINE TRANSFER FROM CHK').getByTestId('tx-status')).toHaveText(
		'Transfer ← Everyday'
	);
	await out.getByRole('button').click();
	await expect(sheet(page).getByTestId('sheet-state')).toContainText('Transfer → Savings');
	await expect(sheet(page).getByRole('button', { name: /Not a transfer/ })).toBeVisible();
	await page.keyboard.press('Escape');

	// one-sided: the brokerage transfer is only a suggestion; one tap marks it (no other side)
	const brokerage = reviewRow(page, 'TRANSFER TO BROKERAGE');
	await expect(brokerage.getByTestId('review-primary')).toHaveText('Transfer');
	await brokerage.getByTestId('review-primary').click();
	await expect(page.getByText('Marked as a transfer')).toBeVisible();

	// the payee was learned: the next brokerage transfer is ignored automatically
	await bankSync(page, email, 'brokerage');
	await page.reload();
	await expect(reviewRow(page, 'TRANSFER TO BROKERAGE')).toHaveCount(0);
	const second = txRow(page, 'TRANSFER TO BROKERAGE').filter({ hasText: '$200.00' });
	await expect(second.getByTestId('tx-auto')).toBeVisible();
	await second.getByRole('button').click();
	await sheet(page)
		.getByRole('button', { name: /Not a transfer/ })
		.click();
	await expect(page.getByText('Not a transfer any more')).toBeVisible();
	await expect(reviewRow(page, 'TRANSFER TO BROKERAGE')).toHaveCount(1);
});

test('account transactions tab: filters, search, the sheet', async ({ page }) => {
	const { accountUrl } = await setup(page);

	await page.goto(accountUrl);
	await page.getByRole('tab', { name: 'Transactions' }).click();
	await expect(page).toHaveURL(/tab=transactions/);
	await expect(page.getByTestId('tx-available')).toHaveText(
		/Available \$2,459\.00\s*·\s*\$41\.00 pending/
	);
	const rows = page.getByTestId('tx-row');
	await expect(txRow(page, 'KROGER #456').getByTestId('tx-status')).toHaveText('To review');
	await expect(txRow(page, 'THE DINER').getByTestId('tx-status')).toHaveText('Pending');
	await expect(txRow(page, 'ONLINE TRANSFER TO SAV').getByTestId('tx-status')).toContainText(
		'Transfer'
	);

	const filter = (name: string) =>
		page.getByRole('radiogroup', { name: 'Filter' }).getByRole('radio', { name, exact: true });
	await filter('Pending').click();
	await expect(txRow(page, 'THE DINER')).toHaveCount(1);
	await expect(txRow(page, 'KROGER #456')).toHaveCount(0);
	await filter('To review').click();
	await expect(txRow(page, 'KROGER #456')).toHaveCount(1);
	await expect(txRow(page, 'ONLINE TRANSFER TO SAV')).toHaveCount(0);
	await filter('Transfers').click();
	await expect(txRow(page, 'ONLINE TRANSFER TO SAV')).toHaveCount(1);
	await expect(rows).toHaveCount(1);

	// debounced search over description / payee / memo
	await filter('All').click();
	await page.getByLabel('Search transactions').fill('kroger');
	await expect(rows).toHaveCount(1);
	await page.getByLabel('Search transactions').fill('');
	await expect(txRow(page, 'PLANET FITNESS')).toBeVisible();

	// a bank row offers every action
	await txRow(page, 'KROGER #456').getByRole('button').click();
	await sheet(page).getByRole('button', { name: 'Groceries', exact: true }).click();
	await expect(page.getByText('Expense added in Groceries')).toBeVisible();
	await expect(txRow(page, 'KROGER #456').getByTestId('tx-status')).toHaveText('Groceries');

	await filter('Matched').click();
	await expect(txRow(page, 'KROGER #456')).toHaveCount(1);

	// a link with the Pending filter opens on it
	await page.goto(`${accountUrl}?tab=transactions&filter=pending`);
	await expect(filter('Pending')).toHaveAttribute('aria-checked', 'true');
	await expect(txRow(page, 'THE DINER')).toHaveCount(1);
	await expect(txRow(page, 'KROGER #456')).toHaveCount(0);
});

test('matching: match Rent from the sheet, unmatch', async ({ page }) => {
	await setup(page);
	const firstOfMonth = `${todayIso().slice(0, 7)}-01`;
	// created after the sync: the new bill is re-checked against To review and suggested
	const rentUrl = await addBill(page, { name: 'Rent', amount: '1850', firstDue: firstOfMonth });

	await page.goto('/transactions');
	const rent = reviewRow(page, 'RENT PAYMENT');
	await expect(rent.getByTestId('review-primary')).toHaveText("It's Rent");
	await rent.getByTestId('review-else').click();
	await sheet(page)
		.getByRole('button', { name: /Match to a bill or income/ })
		.click();
	const candidate = sheet(page).getByTestId('match-candidate').filter({ hasText: 'Rent' });
	await expect(candidate.getByRole('meter')).toHaveAttribute('aria-label', /Strong match/);
	await candidate.click();
	await expect(page.getByText('Matched to Rent')).toBeVisible();
	await expect(txRow(page, 'RENT PAYMENT').getByTestId('tx-status')).toHaveText('Rent');

	// the bill's payment opens the same sheet
	await page.goto(rentUrl);
	await page
		.locator(`[data-testid="payment-row"][data-date="${firstOfMonth}"]`)
		.getByTestId('payment-source')
		.click();
	await expect(sheet(page).getByTestId('sheet-state')).toContainText('Paid Rent');
	await page.keyboard.press('Escape');

	// Unmatch from the sheet: back to review, the bill is unpaid again
	await page.goto('/transactions');
	await txRow(page, 'RENT PAYMENT').getByRole('button').click();
	await expect(sheet(page).getByTestId('sheet-state')).toContainText('Paid Rent');
	await sheet(page)
		.getByRole('button', { name: /^Unmatch/ })
		.click();
	await expect(page.getByText('Back in To review')).toBeVisible();
	await expect(reviewRow(page, 'RENT PAYMENT')).toHaveCount(1);
	await page.goto(rentUrl);
	await expect(
		page.locator(`[data-testid="payment-row"][data-date="${firstOfMonth}"]`)
	).toHaveAttribute('data-paid', 'false');
});

test('bills: create one from a transaction, link a transaction from the bill side', async ({
	page
}) => {
	await setup(page);
	const firstOfMonth = `${todayIso().slice(0, 7)}-01`;
	const rentUrl = await addBill(page, { name: 'Rent', amount: '1800', firstDue: firstOfMonth });

	// Create a bill from this: name prefilled from the bank row, matched to the first occurrence
	await page.goto('/transactions');
	await reviewRow(page, 'PLANET FITNESS').getByTestId('review-else').click();
	await sheet(page)
		.getByRole('button', { name: /Create a bill from this/ })
		.click();
	await expect(sheet(page).getByLabel('Bill name')).toHaveValue('PLANET FITNESS');
	await sheet(page).getByLabel('Bill name').fill('Gym');
	await sheet(page).getByRole('button', { name: 'Create bill' }).click();
	await expect(page.getByText('Gym created and matched')).toBeVisible();
	await sheet(page).getByTestId('open-new-bill').click();
	await expect(page).toHaveURL(/\/bills\/[0-9a-f-]+/);
	const paid = page.locator('[data-testid="payment-row"][data-paid="true"]');
	await expect(paid).toHaveCount(1);
	await expect(paid.getByTestId('payment-source')).toContainText('PLANET FITNESS · Everyday');
	await expect(paid.getByTestId('payment-amount')).toHaveText('$24.99');

	// Link a transaction for an unpaid occurrence: the rent row, $50 over plan
	await page.goto(rentUrl);
	const due = page.locator(`[data-testid="payment-row"][data-date="${firstOfMonth}"]`);
	await expect(due).toHaveAttribute('data-paid', 'false');
	await due.getByRole('button', { name: /Link a transaction/ }).click();
	await page
		.getByRole('dialog')
		.getByTestId('link-candidate')
		.filter({ hasText: 'RENT PAYMENT' })
		.click();
	await expect(page.getByText(/Rent · .* paid from the bank/)).toBeVisible();
	await expect(due).toHaveAttribute('data-paid', 'true');
	await expect(due.getByTestId('variance-pill')).toHaveText('+$50.00');
	await expect(due.getByTestId('payment-source')).toContainText('RENT PAYMENT');

	// the payment opens the shared sheet, which can unmatch it
	await due.getByTestId('payment-source').click();
	await expect(sheet(page).getByTestId('sheet-state')).toContainText('Paid Rent');
	await sheet(page)
		.getByRole('button', { name: /^Unmatch/ })
		.click();
	await expect(page.getByText('Back in To review')).toBeVisible();
	await expect(due).toHaveAttribute('data-paid', 'false');
});
