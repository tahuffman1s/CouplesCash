import { expect, test, type Page } from '@playwright/test';
import { MOCK_API, addAccount, registerWithHousehold, todayIso } from './helpers';

/**
 * v5: pending lifecycle, transfers, per-account transactions and the shared transaction sheet.
 * The `demo-v5` bank (checking + savings with a pending diner bill, a fuel hold, a transfer pair, a
 * one-sided brokerage transfer and a gym charge) and the `?phase=` Bridge edits only exist in the mock
 * API, so every test skips itself against a real API (same rule as banks.e2e.ts); the real paths are
 * covered by apps/api/scripts/smoke.sh with the fake SimpleFIN server.
 */
async function skipUnlessMockBridge(page: Page) {
	await page.goto('/settings/banks');
	await expect(page.getByRole('heading', { name: 'How bank sync works' })).toBeVisible();
	await page.getByLabel('Setup token').fill('probe');
	await page.getByRole('button', { name: 'Link bank' }).click();
	await expect(page.getByRole('alert').or(page.getByTestId('banks-not-configured'))).toBeVisible();
	const notConfigured = await page.getByTestId('banks-not-configured').count();
	test.skip(notConfigured > 0, 'bank sync not configured on this API (demo bank is mock-only)');
	// the v5 data set is mock-only too: a real API rejects the token as invalid
	await page.goto('/settings/banks');
	await page.getByLabel('Name').fill('Demo Bank');
	await page.getByLabel('Setup token').fill('demo-v5');
	await page.getByRole('button', { name: 'Link bank' }).click();
	const linked = page.getByText(/Linked Demo Bank · 2 accounts found/);
	await expect(linked.or(page.getByRole('alert'))).toBeVisible();
	test.skip((await linked.count()) === 0, 'the demo-v5 bank only exists in the mock API');
	await expect(page.getByTestId('external-account')).toHaveCount(2);
}

async function mapChecking(page: Page, accountName = 'Everyday') {
	await page
		.getByLabel('Mapped account for Demo Bank · Everyday Checking')
		.selectOption({ label: accountName });
	await expect(page.getByText(`Everyday Checking now updates ${accountName}`)).toBeVisible();
}

async function createSavings(page: Page) {
	await page.getByLabel('Mapped account for Demo Bank · Way2Save Savings').selectOption('__create');
	await page.getByLabel('New account name').fill('Savings');
	await page.getByRole('button', { name: 'Create & map' }).click();
	await expect(page.getByText('Way2Save Savings now updates Savings')).toBeVisible();
}

/** Runs the mock scheduler's bank sync for this user's household after a Bridge edit. */
async function bankSync(page: Page, email: string, phase: string) {
	const res = await page.request.post(
		`${MOCK_API}/__mock/bank-sync?phase=${phase}&email=${encodeURIComponent(email)}`
	);
	expect(res.ok()).toBeTruthy();
}

async function addBill(page: Page, name: string, amount: string, firstDue: string) {
	await page.goto('/bills/new');
	await page.getByLabel('Name').fill(name);
	await page.getByLabel('Amount').fill(amount);
	await page.getByLabel('Repeats').selectOption('monthly');
	await page.getByLabel('First due date').fill(firstDue);
	await page.getByRole('button', { name: 'Save', exact: true }).click();
	await expect(page).toHaveURL(/\/bills\/[0-9a-f-]+/);
	return page.url();
}

function shiftDays(iso: string, n: number): string {
	const [y, m, d] = iso.split('-').map(Number);
	const date = new Date(y, m - 1, d + n);
	const pad = (x: number) => (x < 10 ? `0${x}` : `${x}`);
	return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

const inboxRow = (page: Page, text: string) =>
	page.getByTestId('inbox-row').filter({ hasText: text });
const sheet = (page: Page) => page.getByTestId('transaction-sheet');

test('pending lifecycle: a tipped bill posts in place, a dropped hold disappears', async ({
	page
}) => {
	const { email } = await registerWithHousehold(page);
	await addAccount(page, 'Everyday', '1000');
	const accountUrl = page.url().split('?')[0];
	await skipUnlessMockBridge(page);
	await mapChecking(page);

	// the authorization is pending at −$40.00; confirm it as a Fun expense from the sheet
	await page.goto('/inbox');
	const diner = inboxRow(page, 'THE DINER');
	await expect(diner).toContainText('Pending');
	await expect(diner.getByTestId('inbox-amount')).toHaveText('−$40.00');
	await diner.getByTestId('inbox-open').click();
	await expect(sheet(page).getByTestId('sheet-pending')).toBeVisible();
	await expect(sheet(page).getByTestId('sheet-state')).toHaveText('To review');
	await expect(sheet(page)).toContainText('Everyday');
	await sheet(page).getByRole('button', { name: 'Fun', exact: true }).click();
	await expect(page.getByText('Expense added in Fun')).toBeVisible();
	await expect(diner.getByTestId('inbox-outcome')).toContainText('Expense in Fun');

	// accounts list: Everyday shows the pending outflow (diner $40 + fuel hold $1)
	await page.goto('/accounts');
	await expect(page.getByTestId('account-pending')).toHaveText('$41.00 pending');

	// the bank posts it with the tip under a new id → the same row, now −$48.00, still confirmed
	await bankSync(page, email, 'post');
	await page.goto('/inbox?status=confirmed');
	await expect(inboxRow(page, 'THE DINER')).toHaveCount(1);
	await expect(inboxRow(page, 'THE DINER').getByTestId('inbox-amount')).toHaveText('−$48.00');
	await expect(inboxRow(page, 'THE DINER')).not.toContainText('Pending');
	await expect(inboxRow(page, 'THE DINER').getByTestId('inbox-outcome')).toContainText(
		'Expense in Fun'
	);
	await page.goto('/inbox');
	await expect(inboxRow(page, 'THE DINER')).toHaveCount(0);
	// the expense followed the posted amount
	await page.goto(`/bills?tab=expenses&m=${todayIso().slice(0, 7)}`);
	await expect(page.getByRole('link', { name: /THE DINER/ })).toContainText('$48.00');

	// the fuel hold is on the account until the bank drops it
	const holdMonth = shiftDays(todayIso(), -6).slice(0, 7);
	await page.goto(`${accountUrl}?tab=transactions&month=${holdMonth}`);
	await expect(page.getByTestId('tx-row').filter({ hasText: 'SHELL OIL' })).toHaveCount(1);
	await bankSync(page, email, 'drop');
	await page.reload();
	await expect(page.getByTestId('tx-month')).toBeVisible();
	await expect(page.getByTestId('tx-row').filter({ hasText: 'SHELL OIL' })).toHaveCount(0);
	await page.goto('/inbox');
	await expect(inboxRow(page, 'SHELL OIL')).toHaveCount(0);
	// …and the Ignored filter says why
	await page.goto('/inbox?status=ignored');
	await expect(inboxRow(page, 'SHELL OIL').getByTestId('inbox-outcome')).toHaveText(
		'Dropped authorization'
	);
	await page.goto('/accounts');
	await expect(page.getByTestId('account-pending')).toHaveCount(0);
});

test('transfers: auto-ignored pair, report, one-sided + learned payee, setting off', async ({
	page
}) => {
	const { email } = await registerWithHousehold(page);
	await addAccount(page, 'Everyday', '1000');
	await skipUnlessMockBridge(page);
	await mapChecking(page);
	await createSavings(page);

	// both sides of the pair were ignored automatically, labelled with the reason and the other side
	await page.goto('/inbox?status=ignored');
	const out = inboxRow(page, 'ONLINE TRANSFER TO SAV REF 123');
	await expect(out.getByTestId('inbox-outcome')).toHaveText('Transfer · auto');
	await expect(out.getByTestId('inbox-account')).toContainText('→ Savings');
	await expect(
		inboxRow(page, 'ONLINE TRANSFER FROM CHK').getByTestId('inbox-account')
	).toContainText('← Everyday');

	// More → Transfers: flow, total, net by account, pairs
	await page.goto('/more');
	await page.getByRole('link', { name: /Transfers/ }).click();
	await expect(page).toHaveURL(/\/transfers$/);
	await expect(page.getByTestId('transfer-flow')).toHaveText(/Everyday → Savings · \$500\.00/);
	await expect(page.getByTestId('transfers-total')).toHaveText('$500.00');
	await expect(page.getByTestId('transfer-pair')).toHaveCount(1);
	await expect(page.getByTestId('net-row')).toHaveCount(2);
	await page.getByRole('button', { name: 'Table' }).click();
	await expect(page.getByRole('table', { name: /Transfers in and out/ })).toContainText('Savings');
	// a pair side opens the shared sheet with "Not a transfer"
	await page.getByRole('button', { name: /Open Everyday side/ }).click();
	await expect(sheet(page).getByTestId('sheet-state')).toContainText('Transfer → Savings');
	await expect(sheet(page).getByRole('button', { name: /Not a transfer/ })).toBeVisible();
	await page.keyboard.press('Escape');

	// one-sided: the brokerage transfer is only a suggestion (never auto, never bulk)
	await page.goto('/inbox');
	const brokerage = inboxRow(page, 'TRANSFER TO BROKERAGE');
	await expect(brokerage.getByTestId('suggestion-chip')).toContainText('Transfer');
	await brokerage.getByRole('button', { name: 'Mark TRANSFER TO BROKERAGE as a transfer' }).click();
	await expect(sheet(page).getByTestId('transfer-peers')).toBeVisible();
	await sheet(page).getByTestId('transfer-other').click();
	await expect(page.getByText('Marked as a transfer')).toBeVisible();
	await page.goto('/transfers');
	await expect(page.getByTestId('one-sided')).toHaveCount(1);
	await expect(page.getByTestId('one-sided')).toContainText('Everyday → Other account');
	await expect(page.getByTestId('transfers-total')).toHaveText('$1,500.00');

	// the payee was learned: the next brokerage transfer is ignored automatically
	await bankSync(page, email, 'brokerage');
	await page.reload();
	await expect(page.getByTestId('one-sided')).toHaveCount(2);
	await expect(page.getByTestId('one-sided').filter({ hasText: '$200.00' })).toContainText('auto');
	// "Not a transfer" sends it back to review
	await page
		.getByTestId('one-sided')
		.filter({ hasText: '$200.00' })
		.getByRole('button', { name: 'Not a transfer' })
		.click();
	await expect(page.getByText('Back to review in the inbox')).toBeVisible();
	await expect(page.getByTestId('one-sided')).toHaveCount(1);

	// Settings → Household: switch auto-ignore off (persists across a reload)
	await page.goto('/settings/household');
	const toggle = page.getByRole('checkbox', {
		name: 'Automatically ignore transfers between linked accounts'
	});
	await expect(toggle).toBeChecked();
	await toggle.click();
	await expect(page.getByText('Household settings saved')).toBeVisible();
	await page.reload();
	await expect(toggle).not.toBeChecked();

	// the next pair stays in the inbox as a suggestion; confirming it picks the other side
	await bankSync(page, email, 'pair');
	await page.goto('/inbox');
	const next = inboxRow(page, 'ONLINE TRANSFER TO SAV REF 456');
	await expect(next).toHaveAttribute('data-status', 'pending');
	await expect(next.getByTestId('suggestion-chip')).toContainText('Transfer');
	await next.getByRole('button', { name: /as a transfer/ }).click();
	await sheet(page).getByTestId('transfer-peer').filter({ hasText: 'Savings' }).click();
	await expect(page.getByText('Marked as a transfer with Savings')).toBeVisible();
	await page.goto('/transfers');
	await expect(
		page.getByTestId('transfer-flow').filter({ hasText: 'Everyday → Savings' })
	).toContainText('$750.00');

	// Insights links to the analysis
	await page.goto('/insights');
	await expect(page.getByTestId('transfers-link')).toBeVisible();
});

test('account transactions tab: filters, search, manual expense, the sheet', async ({ page }) => {
	await registerWithHousehold(page);
	await addAccount(page, 'Everyday', '1000');
	const accountUrl = page.url().split('?')[0];
	await skipUnlessMockBridge(page);
	await mapChecking(page);

	// a typed expense on the same account shows up next to the bank rows
	await page.goto('/expenses/new');
	await page.getByLabel('What was it?').fill('Lunch with Sam');
	await page.getByLabel('Amount').fill('12.50');
	await page.getByLabel('Date').fill(todayIso());
	await page.getByLabel('Category').selectOption({ label: 'Fun' });
	await page.getByLabel('Account').selectOption({ label: 'Everyday' });
	await page.getByRole('button', { name: 'Save expense' }).click();
	await expect(page).toHaveURL(/\/bills\?tab=expenses/);

	await page.goto(accountUrl);
	await page.getByRole('tab', { name: 'Transactions' }).click();
	await expect(page).toHaveURL(/tab=transactions/);
	await expect(page.getByTestId('tx-available')).toHaveText(
		/Available \$2,459\.00\s*·\s*\$41\.00 pending/
	);
	const rows = page.getByTestId('tx-row');
	const txRow = (text: string) => rows.filter({ hasText: text });
	await expect(txRow('Lunch with Sam').getByTestId('tx-status')).toHaveText('Fun');
	await expect(txRow('KROGER #456').getByTestId('tx-status')).toHaveText('To review');
	await expect(txRow('THE DINER').getByTestId('tx-status')).toHaveText('Pending');
	await expect(txRow('ONLINE TRANSFER TO SAV').getByTestId('tx-status')).toContainText('Transfer');
	await expect(page.getByTestId('tx-day').first().getByTestId('tx-day-net')).toBeVisible();

	const filter = (name: string) =>
		page.getByRole('radiogroup', { name: 'Filter' }).getByRole('radio', { name, exact: true });
	await filter('Pending').click();
	await expect(txRow('THE DINER')).toHaveCount(1);
	await expect(txRow('KROGER #456')).toHaveCount(0);
	await filter('To review').click();
	await expect(txRow('KROGER #456')).toHaveCount(1);
	await expect(txRow('Lunch with Sam')).toHaveCount(0);
	await expect(txRow('ONLINE TRANSFER TO SAV')).toHaveCount(0);
	await filter('Matched').click();
	await expect(txRow('Lunch with Sam')).toHaveCount(1);
	await expect(txRow('KROGER #456')).toHaveCount(0);
	await filter('Transfers').click();
	await expect(txRow('ONLINE TRANSFER TO SAV')).toHaveCount(1);
	await expect(rows).toHaveCount(1);

	// debounced search over description / payee / memo
	await filter('All').click();
	await page.getByLabel('Search transactions').fill('kroger');
	await expect(rows).toHaveCount(1);
	await expect(txRow('KROGER #456')).toBeVisible();
	await page.getByLabel('Search transactions').fill('');
	await expect(txRow('Lunch with Sam')).toBeVisible();

	// a manual expense only offers Edit; a bank row offers every action
	await txRow('Lunch with Sam').getByRole('button').click();
	await expect(sheet(page).getByTestId('edit-expense')).toBeVisible();
	await expect(sheet(page).getByRole('button', { name: /Match to a bill/ })).toHaveCount(0);
	await page.keyboard.press('Escape');
	await txRow('KROGER #456').getByRole('button').click();
	await sheet(page).getByRole('button', { name: 'Groceries', exact: true }).click();
	await expect(page.getByText('Expense added in Groceries')).toBeVisible();
	await expect(txRow('KROGER #456').getByTestId('tx-status')).toHaveText('Groceries');

	// month navigation
	await page.getByRole('link', { name: 'Previous month' }).click();
	await expect(page).toHaveURL(/month=\d{4}-\d{2}/);
	await expect(page.getByTestId('tx-month')).not.toHaveText(/^$/);
});

test('matching: confirm Kroger, match Rent from the sheet, unmatch', async ({ page }) => {
	await registerWithHousehold(page);
	await addAccount(page, 'Everyday', '1000');
	await skipUnlessMockBridge(page);
	await mapChecking(page);
	const firstOfMonth = `${todayIso().slice(0, 7)}-01`;
	// created after the sync, so the rent row has no suggestion: it is matched by hand
	await addBill(page, 'Rent', '1850', firstOfMonth);

	await page.goto('/inbox');
	await inboxRow(page, 'KROGER #456').getByRole('button', { name: 'Confirm KROGER #456' }).click();
	await expect(page.getByText('Expense added in Groceries')).toBeVisible();

	const rent = inboxRow(page, 'RENT PAYMENT');
	await rent.getByTestId('inbox-open').click();
	await sheet(page)
		.getByRole('button', { name: /Match to a bill or income/ })
		.click();
	const candidate = sheet(page).getByTestId('match-candidate').filter({ hasText: 'Rent' });
	await expect(candidate.getByRole('meter')).toHaveAttribute('aria-label', /Strong match/);
	await candidate.click();
	await expect(page.getByText('Matched to Rent')).toBeVisible();
	await expect(rent.getByTestId('inbox-outcome')).toContainText('Paid Rent');

	await page.goto(`/calendar?m=${todayIso().slice(0, 7)}`);
	await page.locator(`[data-date="${firstOfMonth}"]`).click();
	await expect(
		page.getByRole('dialog').getByRole('button', { name: 'Mark Rent unpaid' })
	).toHaveAttribute('aria-pressed', 'true');

	// week view: spendable is the bank's available balance; the bank-paid bill opens the same sheet
	await page.goto(`/week?start=${firstOfMonth}`);
	await expect(page.getByTestId('spendable-label')).toHaveText('Available now');
	await page
		.getByTestId('occurrence-row')
		.filter({ hasText: 'Rent' })
		.getByTestId('from-bank')
		.click();
	await expect(sheet(page).getByTestId('sheet-state')).toContainText('Paid Rent');
	await page.keyboard.press('Escape');

	// Unmatch from the sheet: back to review, the bill is unpaid again
	await page.goto('/inbox?status=confirmed');
	await inboxRow(page, 'RENT PAYMENT').getByTestId('inbox-open').click();
	await expect(sheet(page).getByTestId('sheet-state')).toContainText('Paid Rent');
	await sheet(page)
		.getByRole('button', { name: /^Unmatch/ })
		.click();
	await expect(page.getByText('Back to review')).toBeVisible();
	await page.goto(`/calendar?m=${todayIso().slice(0, 7)}`);
	await page.locator(`[data-date="${firstOfMonth}"]`).click();
	await expect(
		page.getByRole('dialog').getByRole('button', { name: 'Mark Rent paid' })
	).toHaveAttribute('aria-pressed', 'false');
});

test('bills: create one from a transaction, link a transaction from the bill side', async ({
	page
}) => {
	await registerWithHousehold(page);
	await addAccount(page, 'Everyday', '1000');
	await skipUnlessMockBridge(page);
	await mapChecking(page);
	const firstOfMonth = `${todayIso().slice(0, 7)}-01`;
	const rentUrl = await addBill(page, 'Rent', '1800', firstOfMonth);

	// Create a bill from this: name prefilled from the bank row, matched to the first occurrence
	await page.goto('/inbox');
	await inboxRow(page, 'PLANET FITNESS').getByTestId('inbox-open').click();
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
	await expect(page.getByText('Back to review')).toBeVisible();
	await expect(due).toHaveAttribute('data-paid', 'false');
});
