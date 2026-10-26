import { expect, test } from '@playwright/test';
import {
	addBill,
	hydrated,
	payWeekStart,
	quickAdd,
	registerWithHousehold,
	reviewRow,
	setupHousehold,
	shiftDays,
	skipUnlessMockBridge,
	todayIso,
	txRow,
	usd
} from './helpers';

/**
 * v7 Friday plan on the mock's household fixture: US Bank available $2,345.67 (3 pending, −$62.40),
 * a weekly $2,450 paycheck and $20 + $150 fun money each, planned by the setup.
 */
const AVAILABLE = 234567;
const cents = (text: string) => Math.round(Number(text.replace(/[^\d.]/g, '')) * 100);

function expectedFun(start: string): number {
	const end = shiftDays(start, 6);
	// the $150s are due on the 1st: they count when a 1st falls in this pay week
	let monthly = 0;
	for (let d = start; d <= end; d = shiftDays(d, 1)) if (d.endsWith('-01')) monthly = 30000;
	return 4000 + monthly;
}

test('the plan reads like her note and its math follows bills paid and one-offs added', async ({
	page,
	context
}) => {
	await context.grantPermissions(['clipboard-read', 'clipboard-write']);
	await registerWithHousehold(page, 'Travis');
	await skipUnlessMockBridge(page);
	const today = todayIso();
	const start = payWeekStart(today, 5);
	await addBill(page, { name: 'Water', amount: '60', firstDue: today });
	// due next Friday: not before next Friday, so not in this week's math
	await addBill(page, { name: 'Rent', amount: '1850', firstDue: shiftDays(start, 7) });
	await setupHousehold(page);

	const fun = expectedFun(start);
	// this week's paycheck (due on payday) counts until the bank shows it arrived
	const income = 245000;
	const receipt = page.getByTestId('plan-receipt');
	// last week's leftover, frozen at payday and not moved yet, is not this week's money
	const sweepLine = page.getByTestId('plan-amount-sweep');
	const sweepOut = (await sweepLine.count()) ? cents(await sweepLine.innerText()) : 0;
	if (sweepOut)
		await expect(receipt.getByTestId('plan-row-sweep')).toContainText('Move to savings');
	let left = AVAILABLE - sweepOut - 6000 - fun + income;

	await expect(page.getByTestId('week-label')).toHaveText(
		/^Fri \w{3} \d{1,2} – Thu \w{3} \d{1,2}$/
	);
	await expect(receipt.getByTestId('plan-row-available')).toContainText('U.S. Bank, available now');
	await expect(page.getByTestId('plan-amount-available')).toHaveText('$2,345.67');
	await expect(page.getByTestId('pending-subline')).toHaveText('after 3 pending · −$62.40');
	await expect(receipt.getByTestId('plan-row-bills')).toContainText('Bills before next Friday');
	await expect(page.getByTestId('plan-amount-bills')).toHaveText('−$60.00');
	await expect(page.getByTestId('plan-amount-fun')).toHaveText(`−${usd(fun)}`);
	await expect(receipt).toContainText('Left on Thursday');
	await expect(page.getByTestId('left-on')).toHaveText(usd(left));
	await expect(page.getByTestId('review-line')).toContainText('to review');

	// expanding a line lists what's in it; the pending list explains itself
	await receipt.getByTestId('plan-row-available').locator('summary').click();
	await expect(receipt.getByTestId('plan-pending').locator('li')).toHaveCount(3);
	const bills = receipt.getByTestId('plan-row-bills');
	await bills.locator('summary').click();
	await expect(bills.getByTestId('plan-line')).toHaveCount(1);
	await expect(bills.getByTestId('plan-line')).toContainText('Water');
	await expect(bills.getByTestId('plan-line')).not.toContainText('Rent');

	// Copy puts the number she writes down on the clipboard
	await hydrated(page);
	await page.getByTestId('copy-left').click();
	await expect(page.getByText(`Copied ${(left / 100).toFixed(2)}`)).toBeVisible();
	expect(await page.evaluate(() => navigator.clipboard.readText())).toBe((left / 100).toFixed(2));

	// a bill ticked by hand: the money is still in the account until the bank shows it, so the
	// number she writes down does not move (it never jumps up)
	await bills.getByRole('button', { name: 'Mark Water paid' }).click();
	await expect(bills.getByTestId('plan-status')).toHaveText('Paid · waiting for the bank');
	await expect(page.getByTestId('left-on')).toHaveText(usd(left));
	await expect(page.getByTestId('plan-amount-bills')).toHaveText('−$60.00');

	// a one-off from one sentence joins it
	await quickAdd(page, 'oil change 60 today');
	left -= 6000;
	await expect(page.getByTestId('plan-amount-oneoffs')).toHaveText('−$60.00');
	await expect(page.getByTestId('left-on')).toHaveText(usd(left));

	// next week: the arrows move a pay week at a time
	await page.getByRole('link', { name: 'Next week' }).click();
	await expect(page).toHaveURL(new RegExp(`date=${shiftDays(start, 7)}`));
	await expect(page.getByTestId('week-today')).toBeVisible();
	await page.getByTestId('week-today').click();
	await expect(page.getByTestId('left-on')).toHaveText(usd(left));
});

test('fun-money accounts never need review; Settings → Banks shows what the feed sent', async ({
	page
}) => {
	await registerWithHousehold(page, 'Travis');
	await skipUnlessMockBridge(page);
	await setupHousehold(page);

	await page.goto('/transactions');
	await hydrated(page);
	// Pending lists every in-flight row, the ones still to review included
	await expect(page.getByTestId('pending-count')).toHaveText('3');
	await expect(page.getByTestId('pending-section').getByTestId('tx-row')).toHaveCount(3);
	// card spends on the fun accounts are personal: not in To review, not even listed by default
	await expect(reviewRow(page, 'KWIK TRIP')).toHaveCount(1);
	await expect(reviewRow(page, 'CHIPOTLE')).toHaveCount(0);
	await expect(reviewRow(page, 'TRADER JOES')).toHaveCount(0);
	await expect(txRow(page, 'CHIPOTLE')).toHaveCount(0);
	const chips = page.getByRole('radiogroup', { name: 'Account' });
	await expect(chips.getByRole('radio', { name: /Spending Account/ })).toHaveCount(0);
	await page.getByTestId('fun-toggle').click();
	await expect(page).toHaveURL(/fun=1/);
	await expect(txRow(page, 'CHIPOTLE')).toHaveCount(1);
	await expect(txRow(page, 'CHIPOTLE').getByRole('button', { name: /It's|Choose/ })).toHaveCount(0);
	await expect(reviewRow(page, 'CHIPOTLE')).toHaveCount(0);
	await expect(chips.getByRole('radio', { name: /Spending Account ··4411/ })).toBeVisible();

	// pending diagnostics
	await page.goto('/settings#banks');
	const diag = page.getByTestId('feed-diagnostics');
	await expect(diag.getByTestId('feed-line').filter({ hasText: 'U.S. Bank' })).toContainText(
		'sent 3 pending, 5 posted · available balance reported'
	);
	await expect(diag.getByTestId('feed-advice')).toContainText('the bank feed is holding them back');
	await page.getByRole('link', { name: /Linked banks/ }).click();
	await expect(page.locator('[data-external="hh-us"]').getByTestId('feed-line')).toHaveText(
		'Last sync: sent 3 pending, 5 posted · available balance reported'
	);
});
