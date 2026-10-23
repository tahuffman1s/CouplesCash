import { expect, test, type Page } from '@playwright/test';
import { addAccount, registerWithHousehold, todayIso } from './helpers';

/** Links the demo bank at /settings/banks and waits for its two external accounts. */
async function linkDemoBank(page: Page) {
	await page.goto('/settings/banks');
	await expect(page.getByRole('heading', { name: 'How bank sync works' })).toBeVisible();
	await page.getByLabel('Name').fill('Demo Bank');
	await page.getByLabel('Setup token').fill('demo-token');
	await page.getByRole('button', { name: 'Link bank' }).click();
	await expect(page.getByText(/Linked Demo Bank · 2 accounts found/)).toBeVisible();
	await expect(page.getByTestId('external-account')).toHaveCount(2);
}

async function mapChecking(page: Page, accountName: string) {
	await page
		.getByLabel('Mapped account for Demo Bank · Everyday Checking')
		.selectOption({ label: accountName });
	await expect(page.getByText(`Everyday Checking now updates ${accountName}`)).toBeVisible();
}

function shiftDays(iso: string, n: number): string {
	const [y, m, d] = iso.split('-').map(Number);
	const date = new Date(y, m - 1, d + n);
	const pad = (x: number) => (x < 10 ? `0${x}` : `${x}`);
	return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

async function addBill(
	page: Page,
	opts: { kind?: 'Bill' | 'Income'; name: string; amount: string; firstDue: string }
) {
	await page.goto('/bills/new');
	if (opts.kind) await page.getByRole('radiogroup', { name: 'Kind' }).getByText(opts.kind).click();
	await page.getByLabel('Name').fill(opts.name);
	await page.getByLabel('Amount').fill(opts.amount);
	await page.getByLabel('Repeats').selectOption('monthly');
	await page.getByLabel('First due date').fill(opts.firstDue);
	await page.getByRole('button', { name: 'Save', exact: true }).click();
	await expect(page).toHaveURL(/\/bills\/[0-9a-f-]+/);
}

test('link a bank: bad token, demo token, map + create accounts, sync cooldown, unlink', async ({
	page
}) => {
	await registerWithHousehold(page);
	await addAccount(page, 'Everyday', '1000');

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

test('inbox: suggestions, confirm as bill / expense, ignore + restore, bulk confirm', async ({
	page
}) => {
	await registerWithHousehold(page);
	await addAccount(page, 'Everyday', '1000');
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

	// dashboard card → inbox
	await page.goto('/');
	const card = page.getByTestId('inbox-card');
	await expect(card).toContainText('5 transactions to review');
	await expect(card).toContainText('2 suggested');
	await card.click();
	await expect(page).toHaveURL(/\/inbox$/);

	const row = (text: string) => page.getByTestId('inbox-row').filter({ hasText: text });
	await expect(page.getByTestId('pending-count')).toHaveText('5');
	await expect(page.getByTestId('confirm-all')).toContainText('Confirm all suggested (2)');
	await expect(row('RENT PAYMENT').getByTestId('suggestion-chip')).toContainText('Rent · due');
	await expect(row('RENT PAYMENT').getByTestId('inbox-amount')).toHaveText('−$1,850.00');
	await expect(row('KROGER #123').getByTestId('suggestion-chip')).toContainText('Groceries');
	await expect(row('ACME PAYROLL').getByTestId('inbox-amount')).toHaveText('+$3,200.00');
	await expect(row('COFFEE')).toContainText('Pending');

	// confirm Rent with its suggestion → occurrence paid
	await row('RENT PAYMENT').getByRole('button', { name: 'Confirm RENT PAYMENT' }).click();
	await expect(page.getByText('Rent marked paid')).toBeVisible();
	await expect(row('RENT PAYMENT').getByTestId('inbox-outcome')).toContainText('Paid Rent');
	await expect(page.getByTestId('pending-count')).toHaveText('4');

	// confirm Kroger → expense in Groceries
	await row('KROGER #123').getByRole('button', { name: 'Confirm KROGER #123' }).click();
	await expect(page.getByText('Expense added in Groceries')).toBeVisible();
	await expect(row('KROGER #123').getByTestId('inbox-outcome')).toContainText(
		'Expense in Groceries'
	);

	// ignore the pending coffee, then bring it back
	await row('COFFEE').getByRole('button', { name: 'Ignore COFFEE' }).click();
	await expect(row('COFFEE').getByTestId('inbox-outcome')).toContainText('Ignored');
	await expect(page.getByTestId('pending-count')).toHaveText('2');
	await row('COFFEE').getByRole('button', { name: 'Restore COFFEE' }).click();
	await expect(page.getByText('Back in the inbox')).toBeVisible();
	await expect(row('COFFEE').getByRole('button', { name: 'Confirm COFFEE' })).toBeVisible();
	await expect(page.getByTestId('pending-count')).toHaveText('3');

	// change a suggestion through the sheet: coffee → Fun is suggested, pick Groceries instead
	await row('COFFEE').getByTestId('suggestion-chip').click();
	const sheet = page.getByRole('dialog');
	await expect(sheet).toContainText('COFFEE');
	await sheet.getByRole('button', { name: 'Groceries' }).click();
	await expect(row('COFFEE').getByTestId('inbox-outcome')).toContainText('Expense in Groceries');

	// bulk: the payroll row (matched to Paycheck) is the only suggested one left
	await expect(page.getByTestId('confirm-all')).toContainText('Confirm all suggested (1)');
	await page.getByTestId('confirm-all').click();
	await expect(page.getByText('1 confirmed')).toBeVisible();
	await expect(page.getByTestId('pending-count')).toHaveText('1');
	await expect(page.getByTestId('inbox-row')).toHaveCount(1);
	await expect(row('NETFLIX.COM')).toBeVisible();

	// Confirmed filter lists what happened
	await page.getByRole('radio', { name: 'Confirmed' }).click();
	await expect(page).toHaveURL(/status=confirmed/);
	await expect(page.getByTestId('inbox-row')).toHaveCount(4);
	await expect(row('ACME PAYROLL').getByTestId('inbox-outcome')).toContainText('Paid Paycheck');

	// calendar: Rent is paid and carries the From bank badge
	await page.goto(`/calendar?m=${today.slice(0, 7)}`);
	await page.locator(`[data-date="${firstOfMonth}"]`).click();
	const dialog = page.getByRole('dialog');
	await expect(dialog.getByRole('button', { name: 'Mark Rent unpaid' })).toHaveAttribute(
		'aria-pressed',
		'true'
	);
	await expect(dialog.getByTestId('from-bank')).toBeVisible();

	// Bills › Expenses has the Kroger expense; no balance change (deduct = false)
	await page.goto(`/bills?tab=expenses&m=${today.slice(0, 7)}`);
	await expect(page.getByRole('link', { name: /KROGER #123/ })).toBeVisible();
	await page.goto('/accounts');
	await expect(page.getByTestId('net-worth')).toHaveText('$2,345.67');

	// the activity feed shows the new kinds
	await page.goto('/activity');
	await expect(page.locator('main li').filter({ hasText: 'linked bank Demo Bank' })).toBeVisible();
	await expect(page.locator('main li').filter({ hasText: 'matched Rent' })).toBeVisible();

	// More menu badge
	await page.goto('/more');
	await expect(page.getByTestId('inbox-badge')).toHaveText('1');
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
