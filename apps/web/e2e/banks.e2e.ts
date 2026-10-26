import { expect, test } from '@playwright/test';
import {
	addAccount,
	addBill,
	linkDemoBank,
	mapChecking,
	registerWithHousehold,
	reviewRow,
	shiftDays,
	skipUnlessMockBridge,
	todayIso,
	txRow
} from './helpers';

/**
 * The demo bank (`demo-token`) only exists in the mock API's fake Bridge. Against a real API the
 * claim form shows the "not configured" state (no DATA_KEY) or rejects the token, so these flows
 * skip themselves; the real bank-sync path is covered by apps/api/scripts/smoke.sh with a fake
 * SimpleFIN server.
 */

test('link a bank: bad token, demo token, map + create accounts, sync cooldown, unlink', async ({
	page
}) => {
	await registerWithHousehold(page);
	await addAccount(page, 'Everyday', '1000');
	await skipUnlessMockBridge(page);

	// an invalid setup token is a field error, not a crash
	await page.goto('/settings/banks');
	await page.getByLabel('Setup token').fill('nope');
	await page.getByRole('button', { name: 'Link bank' }).click();
	await expect(page.getByRole('alert')).toContainText(/invalid or has expired/i);

	await linkDemoBank(page);
	const link = page.getByTestId('bank-link');
	await expect(link).toContainText('Everyday Checking');
	await expect(link).toContainText('Freedom Visa');
	await expect(link).toContainText('Demo Bank');

	// map the checking account → its balance becomes the external balance (+ snapshot)
	await mapChecking(page, 'Everyday');
	await page.goto('/accounts');
	await expect(page.getByTestId('net-worth')).toHaveText('$2,345.67');

	// "Create account…" opens the prefilled mini-form (type guessed from the bank's name)
	await page.goto('/settings/banks');
	await page.getByLabel('Mapped account for Demo Bank · Freedom Visa').selectOption('__create');
	await expect(page.getByLabel('New account name')).toHaveValue('Freedom Visa');
	await expect(page.getByLabel('New account type')).toHaveValue('credit');
	await page.getByRole('button', { name: 'Create & map' }).click();
	await expect(page.getByText('Freedom Visa now updates Freedom Visa')).toBeVisible();
	await page.goto('/accounts');
	await expect(page.getByRole('link', { name: /Freedom Visa/ })).toBeVisible();
	await expect(page.getByTestId('net-worth')).toHaveText('$1,805.67');

	// Sync now: a result toast, then the 15-minute cooldown
	await page.goto('/settings/banks');
	await page.getByRole('button', { name: 'Sync now' }).click();
	await expect(page.getByText(/balances? updated|Synced recently/)).toBeVisible();
	await page.getByRole('button', { name: 'Sync now' }).click();
	await expect(page.getByText(/Synced recently, try again in \d+ min/)).toBeVisible();

	// rename inline, pause syncing
	await page.getByRole('button', { name: 'Rename Demo Bank' }).click();
	await page.getByLabel('Link name').fill('Our bank');
	await page.getByRole('button', { name: 'Save' }).click();
	await expect(page.getByRole('heading', { name: 'Our bank' })).toBeVisible();
	await page.getByRole('checkbox', { name: /Syncing every few hours/ }).click();
	await expect(page.getByText('Syncing paused')).toBeVisible();
	await expect(page.getByText('Paused', { exact: true })).toBeVisible();

	// unlink through the confirm sheet; mapped accounts survive
	await page.getByRole('button', { name: 'Unlink' }).click();
	const sheet = page.getByRole('dialog');
	await expect(sheet).toContainText('Unlink Our bank?');
	await sheet.getByRole('button', { name: 'Unlink' }).click();
	await expect(page.getByText('Bank unlinked')).toBeVisible();
	await expect(page.getByTestId('bank-link')).toHaveCount(0);
	await page.goto('/accounts');
	await expect(page.getByTestId('net-worth')).toHaveText('$1,805.67');
});

test('To review: suggestions in words, one tap, ignore + undo, do all', async ({ page }) => {
	await registerWithHousehold(page);
	await addAccount(page, 'Everyday', '1000');
	await skipUnlessMockBridge(page);
	const today = todayIso();
	const firstOfMonth = `${today.slice(0, 7)}-01`;

	// the matcher needs the bills to exist before the first sync
	await addBill(page, { name: 'Rent', amount: '1850', firstDue: firstOfMonth });
	await addBill(page, {
		kind: 'Income',
		name: 'Paycheck',
		amount: '3200',
		firstDue: shiftDays(today, -2)
	});

	await linkDemoBank(page);
	await mapChecking(page, 'Everyday');

	// the week sends you to Transactions; the tab badge counts what needs a person
	await page.goto('/week');
	await expect(page.getByTestId('review-banner')).toContainText('5 to review');
	await expect(page.getByTestId('review-badge')).toHaveText('5');
	await page.getByTestId('review-banner').click();
	await expect(page).toHaveURL(/\/transactions$/);

	const row = (text: string) => reviewRow(page, text);
	await expect(page.getByTestId('review-row')).toHaveCount(5);
	await expect(row('RENT PAYMENT').getByTestId('review-primary')).toHaveText("It's Rent");
	await expect(row('RENT PAYMENT').getByTestId('review-amount')).toHaveText('−$1,850.00');
	await expect(row('KROGER #123').getByTestId('review-primary')).toHaveText('Groceries');
	await expect(row('ACME PAYROLL').getByTestId('review-primary')).toHaveText("It's Paycheck");
	await expect(row('ACME PAYROLL').getByTestId('review-amount')).toHaveText('+$3,200.00');
	await expect(row('COFFEE')).toContainText('Pending');
	await expect(page.getByTestId('do-all')).toContainText('Do all suggestions (2)');

	// one tap: Rent is paid, the row moves down to the posted list
	await row('RENT PAYMENT').getByTestId('review-primary').click();
	await expect(page.getByText('Rent marked paid')).toBeVisible();
	await expect(row('RENT PAYMENT')).toHaveCount(0);
	await expect(txRow(page, 'RENT PAYMENT').getByTestId('tx-status')).toHaveText('Rent');
	await expect(page.getByTestId('review-badge')).toHaveText('4');

	// Kroger → expense in Groceries
	await row('KROGER #123').getByTestId('review-primary').click();
	await expect(page.getByText('Expense added in Groceries')).toBeVisible();
	await expect(txRow(page, 'KROGER #123').getByTestId('tx-status')).toHaveText('Groceries');

	// "Something else" opens the sheet: coffee → Groceries instead of the suggested Fun
	await row('COFFEE').getByTestId('review-else').click();
	await expect(page.getByRole('dialog')).toContainText('COFFEE');
	await page
		.getByTestId('transaction-sheet')
		.getByRole('button', { name: 'Groceries', exact: true })
		.click();
	await expect(txRow(page, 'COFFEE').getByTestId('tx-status')).toHaveText('Groceries');

	// do all: the payroll row (matched to Paycheck) is the only confident one left
	await expect(page.getByTestId('do-all')).toContainText('Do all suggestions (1)');
	await page.getByTestId('do-all').click();
	await expect(page.getByText('1 done')).toBeVisible();
	await expect(page.getByTestId('review-row')).toHaveCount(1);
	await expect(row('NETFLIX.COM')).toBeVisible();
	await expect(txRow(page, 'ACME PAYROLL').getByTestId('tx-status')).toHaveText('Paycheck');

	// Bills › Calendar: Rent is paid and carries the From bank badge
	await page.goto(`/bills?view=calendar&m=${today.slice(0, 7)}`);
	await page.locator(`[data-date="${firstOfMonth}"]`).click();
	const dialog = page.getByRole('dialog');
	await expect(dialog.getByRole('button', { name: 'Mark Rent unpaid' })).toHaveAttribute(
		'aria-pressed',
		'true'
	);
	await expect(dialog.getByTestId('from-bank')).toBeVisible();

	// no balance change from confirming an expense (deduct = false)
	await page.goto('/accounts');
	await expect(page.getByTestId('net-worth')).toHaveText('$2,345.67');

	// the activity feed shows the new kinds
	await page.goto('/activity');
	await expect(page.locator('main li').filter({ hasText: 'linked bank Demo Bank' })).toBeVisible();
	await expect(page.locator('main li').filter({ hasText: 'matched Rent' })).toBeVisible();

	// the old inbox URL lands on Transactions
	await page.goto('/inbox');
	await expect(page).toHaveURL(/\/transactions$/);
	await expect(page.getByTestId('review-badge')).toHaveText('1');
});

test('bank sync not configured: the claim form explains the missing DATA_KEY', async ({ page }) => {
	await registerWithHousehold(page);
	await page.goto('/settings/banks');
	// against a real API without Security__DataKey the whole page is already in this state
	if ((await page.getByTestId('banks-not-configured').count()) === 0) {
		// the mock answers 503 for this token (MOCK_BANKS_DISABLED=1 does it for every claim)
		await page.getByLabel('Setup token').fill('demo-not-configured');
		await page.getByRole('button', { name: 'Link bank' }).click();
	}
	await expect(page.getByTestId('banks-not-configured')).toContainText('DATA_KEY');
	await expect(page.getByTestId('banks-not-configured')).toContainText('docs/BANK-SYNC.md');
});
