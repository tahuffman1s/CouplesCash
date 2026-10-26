import { expect, test, type Page } from '@playwright/test';
import {
	MOCK_API,
	PASSWORD,
	addAccount,
	registerWithHousehold,
	shiftDays,
	todayIso,
	uniqueEmail
} from './helpers';

/**
 * v4 week view (v6: it is home), planned one-offs that reconcile themselves from the bank, weekly
 * budgets and the two settings. Runs against the mock API; the bank part skips itself when the API
 * has no fake Bridge (same rule as banks.e2e.ts).
 */

/** Last day of the current week for a Sunday-start household (the default). */
function weekEndIso(): string {
	const t = todayIso();
	const [y, m, d] = t.split('-').map(Number);
	return shiftDays(t, 6 - new Date(y, m - 1, d).getDay());
}

async function addBill(
	page: Page,
	opts: { kind?: 'Bill' | 'Income'; name: string; amount: string; firstDue: string }
) {
	await page.goto('/bills/new');
	if (opts.kind) await page.getByRole('radiogroup', { name: 'Kind' }).getByText(opts.kind).click();
	await page.getByLabel('Name', { exact: true }).fill(opts.name);
	await page.getByLabel('Amount').fill(opts.amount);
	await page.getByLabel('Repeats').selectOption('monthly');
	await page.getByLabel('First due date').fill(opts.firstDue);
	await page.getByRole('button', { name: 'Save', exact: true }).click();
	await expect(page).toHaveURL(/\/bills\/[0-9a-f-]+/);
}

async function addExpense(
	page: Page,
	opts: { name: string; amount: string; category: string; account?: string }
) {
	await page.goto('/expenses/new');
	await page.getByLabel('What was it?').fill(opts.name);
	await page.getByLabel('Amount').fill(opts.amount);
	await page.getByLabel('Date').fill(todayIso());
	await page.getByLabel('Category').selectOption({ label: opts.category });
	if (opts.account) await page.getByLabel('Account').selectOption({ label: opts.account });
	await page.getByRole('button', { name: 'Save expense' }).click();
	await expect(page).toHaveURL(/\/transactions\?added=1/);
}

async function setWeeklyTarget(page: Page, amount: string) {
	await page.goto('/settings#household');
	await page.getByRole('textbox', { name: 'Weekly spending target' }).fill(amount);
	await page.locator('#weekly').getByRole('button', { name: 'Save' }).click();
	await expect(page.getByText('Saved', { exact: true })).toBeVisible();
}

/** Opens the weekly-budgets sheet on /week and sets one category amount. */
async function setCategoryWeekly(page: Page, category: string, amount: string) {
	await page.goto('/week');
	await page.getByRole('button', { name: 'Set weekly budgets' }).click();
	const sheet = page.getByRole('dialog');
	await sheet.getByLabel(`Weekly budget for ${category}`).fill(amount);
	await sheet.getByRole('button', { name: 'Save weekly budgets' }).click();
	await expect(page.getByText('Weekly budgets saved')).toBeVisible();
	await expect(page.getByRole('dialog')).toHaveCount(0);
}

test('week view: left this week, day list, weekly budgets', async ({ page }) => {
	await registerWithHousehold(page);
	await addAccount(page, 'Everyday', '1000');
	const today = todayIso();
	const weekEnd = weekEndIso();

	// seed: Rent due on the last day of this week, Paycheck today, a Groceries expense today
	await addBill(page, { name: 'Rent', amount: '1850', firstDue: weekEnd });
	await addBill(page, { kind: 'Income', name: 'Paycheck', amount: '3200', firstDue: today });
	await addExpense(page, {
		name: 'Groceries run',
		amount: '45',
		category: 'Groceries',
		account: 'Everyday'
	});

	// settings: weekly target persists (blank would clear it)
	await setWeeklyTarget(page, '300');
	await page.reload();
	await expect(page.getByRole('textbox', { name: 'Weekly spending target' })).toHaveValue('300.00');

	// week view: budgets sheet → Groceries 120
	await setCategoryWeekly(page, 'Groceries', '120');

	// hero: spendable (1000 − 45) + unpaid income 3200 − unpaid planned 1850 = 2305
	await expect(page.locator('#left')).toHaveText('Left this week');
	await expect(page.getByTestId('left-at-end')).toHaveText('$2,305.00');
	await expect(page.getByTestId('left-subtitle')).toHaveText('if nothing else happens');
	await expect(page.getByTestId('stat-spendable')).toHaveText('$955.00');
	await expect(page.getByTestId('stat-pending')).toHaveText('$0.00');
	await expect(page.getByTestId('stat-income')).toHaveText('+$3,200.00');
	await expect(page.getByTestId('stat-planned')).toHaveText('−$1,850.00');
	await expect(page.getByTestId('overdue-section')).toHaveCount(0);

	// day list: Rent sits on its date as Planned; today has the paycheck and the expense
	const rentDay = page.locator(`[data-testid="week-day"][data-date="${weekEnd}"]`);
	const rentRow = rentDay.getByTestId('occurrence-row').filter({ hasText: 'Rent' });
	await expect(rentRow.getByTestId('status-chip')).toHaveText('Planned');
	const todayDay = page.locator(`[data-testid="week-day"][data-date="${today}"]`);
	await expect(todayDay).toContainText('Today');
	await expect(
		todayDay.getByTestId('occurrence-row').filter({ hasText: 'Paycheck' })
	).toBeVisible();
	await expect(todayDay.getByTestId('week-expense')).toContainText('Groceries run');

	// weekly budget: overall 45 / 300 under by 255, Groceries 45 / 120
	await expect(page.getByTestId('week-budget-overall')).toContainText('$45.00');
	await expect(page.getByTestId('week-budget-overall')).toContainText('$300.00');
	await expect(page.getByTestId('week-budget-overall-status')).toHaveText('Under by $255.00');
	await expect(page.getByTestId('week-budget-overall-status')).toHaveClass(/text-success/);
	const groceries = page.locator('[data-testid="week-budget-row"][data-category="Groceries"]');
	await expect(groceries).toContainText('$45.00');
	await expect(groceries).toContainText('$120.00');
	await expect(groceries.getByRole('meter')).toHaveAttribute('aria-valuenow', '4500');
	await expect(page.getByTestId('week-history-bar')).toHaveCount(8);

	// inline mark paid recomputes everything; Undo (unpay) restores it
	await rentRow.getByRole('button', { name: 'Mark Rent paid' }).click();
	await expect(rentRow.getByTestId('status-chip')).toHaveText('Paid');
	await expect(page.getByTestId('left-at-end')).toHaveText('$4,155.00');
	await expect(page.getByTestId('week-budget-overall-status')).toHaveText('Over by $1,595.00');
	await expect(page.getByTestId('week-budget-overall-status')).toHaveClass(/text-error/);
	await rentRow.getByRole('button', { name: 'Mark Rent unpaid' }).click();
	await expect(rentRow.getByTestId('status-chip')).toHaveText('Planned');
	await expect(page.getByTestId('left-at-end')).toHaveText('$2,305.00');

	// week navigation: next week is projected, "Today" brings the current week back
	await page.getByRole('link', { name: 'Next week' }).click();
	await expect(page).toHaveURL(/\/week\?start=\d{4}-\d{2}-\d{2}$/);
	await expect(page.getByTestId('left-subtitle')).toHaveText('projected');
	await page.getByTestId('week-today').click();
	await expect(page).toHaveURL(/\/week$/);
	await expect(page.getByTestId('left-subtitle')).toHaveText('if nothing else happens');

	// Insights shows monthly budgets only (weekly ones live here)
	await page.goto('/insights');
	await expect(page.getByRole('heading', { name: 'Monthly budgets' })).toBeVisible();
	await expect(page.getByRole('radio', { name: 'Weekly' })).toHaveCount(0);

	// over budget: Fun 50, then spend 70 → over by $20 in the error colour
	await setCategoryWeekly(page, 'Fun', '50');
	await addExpense(page, { name: 'Concert', amount: '70', category: 'Fun' });
	await page.goto('/week');
	const fun = page.locator('[data-testid="week-budget-row"][data-category="Fun"]');
	await expect(fun.getByTestId('week-budget-status')).toHaveText('Over by $20.00');
	await expect(fun.getByTestId('week-budget-status')).toHaveClass(/text-error/);
	await expect(fun.getByRole('meter')).toHaveAttribute('aria-label', /over budget/);
	await expect(page.getByTestId('week-budget-overall')).toContainText('$115.00');
	await expect(page.getByTestId('week-budget-total')).toContainText('$115.00');
	await expect(page.getByTestId('week-budget-total')).toContainText('$170.00');
	// the history chart has an over-budget column for the current week and a table fallback
	await expect(page.getByTestId('week-history-bar')).toHaveCount(8);
	await expect(page.locator('[data-testid="week-history-bar"]').last()).toHaveAttribute(
		'data-variance',
		'-18500'
	);
	await page.getByRole('button', { name: 'Table', exact: true }).click();
	await expect(page.getByRole('table', { name: /last 8 weeks/i })).toContainText(
		'under by $185.00'
	);

	// clearing: blank Groceries removes its row
	await page.getByRole('button', { name: 'Set weekly budgets' }).click();
	await page.getByRole('dialog').getByLabel('Weekly budget for Groceries').fill('');
	await page.getByRole('dialog').getByRole('button', { name: 'Save weekly budgets' }).click();
	await expect(page.getByText('Weekly budgets saved')).toBeVisible();
	await expect(
		page.locator('[data-testid="week-budget-row"][data-category="Groceries"]')
	).toHaveCount(0);
});

test('a one-off from one sentence: on the week and the calendar, clears itself from the bank', async ({
	page
}) => {
	await registerWithHousehold(page);
	await addAccount(page, 'Everyday', '1000');
	const today = todayIso();

	// "+" on the week: a date without a frequency is a one-off; the name is the first bank name
	await page.goto('/week');
	await page.getByTestId('quick-add').click();
	await page.getByTestId('quick-add-input').fill('jiffy lube 60 today');
	await expect(page.getByTestId('chip-cadence')).toHaveText('One time');
	await page.getByTestId('quick-add-submit').click();
	await expect(page.getByText(/Jiffy lube added · one time/)).toBeVisible();
	await expect(page.getByTestId('quick-add-form')).toHaveCount(0);

	// Planned, counted against what's left
	const row = page.getByTestId('occurrence-row').filter({ hasText: 'Jiffy lube' });
	await expect(row.getByTestId('status-chip')).toHaveText('Planned');
	await expect(row.getByTestId('occurrence-amount')).toHaveText('−$60.00');
	await expect(row.getByTestId('variance-pill')).toHaveCount(0);
	await expect(page.getByTestId('left-at-end')).toHaveText('$940.00');

	// Bills › Calendar: it is an occurrence like any other
	await page.goto('/bills?view=calendar');
	const day = page.locator(`[data-date="${today}"]`);
	await expect(day).toHaveAttribute('aria-label', /1 item/);
	await day.click();
	await expect(page.getByRole('dialog')).toContainText('Jiffy lube');

	// Bills › One-offs lists it (and the recurring chips don't)
	await page.goto('/bills');
	await expect(page.getByRole('link', { name: /Jiffy lube/ })).toHaveCount(0);
	await page.getByRole('radio', { name: 'One-offs' }).click();
	const oneOff = page.getByTestId('one-offs').getByRole('link', { name: /Jiffy lube/ });
	await expect(oneOff).toBeVisible();
	await expect(oneOff.getByTestId('one-off-status')).toHaveText('Planned');
	await expect(oneOff).toContainText('bank: Jiffy lube');
	// the full form shows the bank name as a chip and labels the date plainly
	await oneOff.click();
	await page.getByRole('button', { name: 'Edit' }).click();
	await expect(page.getByTestId('bank-name-chip')).toHaveText(['Jiffy lube']);
	await expect(page.getByLabel('Date', { exact: true })).toHaveValue(today);

	// --- bank: the demo Bridge only exists in the mock API
	await page.goto('/settings/banks');
	await page.getByLabel('Setup token').fill('probe');
	await page.getByRole('button', { name: 'Link bank' }).click();
	await expect(page.getByRole('alert').or(page.getByTestId('banks-not-configured'))).toBeVisible();
	test.skip(
		(await page.getByTestId('banks-not-configured').count()) > 0,
		'bank sync not configured on this API (demo bank is mock-only)'
	);
	await setWeeklyTarget(page, '300');
	await page.goto('/settings/banks');
	await page.getByLabel('Name').fill('Demo Bank');
	await page.getByLabel('Setup token').fill('demo-token');
	await page.getByRole('button', { name: 'Link bank' }).click();
	await expect(page.getByText(/Linked Demo Bank · 2 accounts found/)).toBeVisible();
	await page
		.getByLabel('Mapped account for Demo Bank · Everyday Checking')
		.selectOption({ label: 'Everyday' });
	await expect(page.getByText('Everyday Checking now updates Everyday')).toBeVisible();

	// the synced balance moves what's left; the one-off is still open
	await page.goto('/week');
	await expect(page.getByTestId('left-at-end')).toHaveText('$2,285.67');
	await expect(page.getByTestId('week-budget-overall')).toContainText('$0.00');

	// scheduler sync: JIFFY LUBE #42 −54.20 lands, fuzzy-matches the bank name (±7 d, 15 %) and
	// auto-applies
	const res = await page.request.post(`${MOCK_API}/__mock/bank-sync`);
	expect(res.ok()).toBeTruthy();
	await page.reload();
	await expect(row.getByTestId('status-chip')).toHaveText('Paid');
	await expect(row.getByTestId('occurrence-amount')).toHaveText('−$54.20');
	await expect(row.getByTestId('variance-pill')).toHaveText('−$5.80');
	await expect(row.getByTestId('variance-pill')).toHaveClass(/text-success/);
	await expect(row.getByTestId('from-bank')).toBeVisible();
	await expect(page.getByTestId('left-at-end')).toHaveText('$2,345.67');
	await expect(page.getByTestId('week-budget-overall')).toContainText('$54.20');
	await expect(page.getByTestId('week-budget-overall-status')).toHaveText('Under by $245.80');

	// the variance pill shows everywhere an OccurrenceRow renders
	await page.goto('/bills?view=calendar');
	await page.locator(`[data-date="${today}"]`).click();
	await expect(page.getByRole('dialog').getByTestId('variance-pill')).toHaveText('−$5.80');

	// One-offs chip says Paid
	await page.goto('/bills?tab=once');
	await expect(page.getByTestId('one-offs').getByTestId('one-off-status')).toHaveText('Paid');

	// nothing to review from that line: it reconciled itself, with an "auto" mark and the reason
	await page.goto('/transactions');
	const tx = page.getByTestId('tx-row').filter({ hasText: 'JIFFY LUBE' });
	await expect(tx.getByTestId('tx-status')).toHaveText('Jiffy lube');
	await expect(tx.getByTestId('tx-auto')).toBeVisible();
	await expect(tx.getByTestId('tx-reason')).toContainText('Auto-matched · name 100%');
});

test('weekly summary toggle persists (Settings › You)', async ({ page }) => {
	// a `push-` email makes the mock serve push prefs for this user only
	const email = uniqueEmail('push');
	await page.goto('/register');
	await page.getByLabel('Your name').fill('Alex');
	await page.getByLabel('Email').fill(email);
	await page.getByLabel('Password', { exact: true }).fill(PASSWORD);
	await page.getByRole('button', { name: 'Create account' }).click();
	await expect(page).toHaveURL(/\/week$/);
	await page.goto('/couple');
	await page.getByLabel('Name your household').fill('Test House');
	await page.getByRole('button', { name: /Create household/ }).click();
	await expect(page.getByTestId('couple-code')).toBeVisible();

	await page.goto('/settings#you');
	test.skip(
		(await page.getByTestId('push-not-configured').count()) > 0,
		'push not configured on this API'
	);
	const toggle = page.getByTestId('week-summary-toggle');
	await expect(toggle).toBeChecked();
	await expect(page.getByText(/Week ahead:/)).toBeVisible();
	await toggle.uncheck();
	await page.getByRole('button', { name: 'Save preferences' }).click();
	await expect(page.getByText('Preferences saved')).toBeVisible();
	await page.reload();
	await expect(page.getByTestId('week-summary-toggle')).not.toBeChecked();
});
