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
	let left = AVAILABLE - 6000 - fun + income;
	// the move to savings is the bottom line (on Thursday), never a line in the calculation
	await expect(receipt.getByTestId('plan-row-sweep')).toHaveCount(0);
	await expect(receipt.getByTestId('sweep-line')).toContainText('to savings');

	await expect(page.getByTestId('week-label')).toHaveText(
		/^Fri \w{3} \d{1,2} – Thu \w{3} \d{1,2}$/
	);
	// the current week is a ledger: start of the week, payday … today (today highlighted), available now, what is still to come
	const ledger = receipt.getByTestId('plan-ledger');
	await expect(ledger.getByTestId('ledger-start')).toContainText('Start of the week');
	const days = ledger.getByTestId('ledger-day');
	await expect(days).toHaveCount(
		Math.round((Date.parse(today) - Date.parse(start)) / 86400000) + 1
	);
	await expect(days.first()).toHaveAttribute('data-date', start);
	await expect(days.last()).toHaveAttribute('data-today', 'true');
	await expect(days.last()).toContainText('Today ·');
	// the days before today are folded up until tapped; today ends in available now
	if (start !== today) {
		await expect(days.first()).not.toHaveAttribute('open', '');
		await days.first().locator('summary').click();
		await expect(days.first()).toHaveAttribute('open', '');
	}
	// today folds up too, but available now stays in sight under it
	await expect(days.last()).not.toHaveAttribute('open', '');
	await expect(ledger.getByTestId('ledger-today').getByTestId('ledger-available')).toBeVisible();
	await expect(ledger.getByTestId('ledger-available')).toHaveText(
		/^\s*Available now\s*\$2,345\.67\s*$/
	);
	await expect(page.getByTestId('plan-amount-available')).toHaveText('$2,345.67');
	// still to come: Water today, the paycheck and fun money (due on payday); next Friday's rent is not in this week
	const upcoming = ledger.getByTestId('ledger-upcoming');
	const water = upcoming.getByTestId('plan-line').filter({ hasText: 'Water' });
	await expect(water).toHaveCount(1);
	await expect(
		upcoming.filter({ has: page.getByTestId('plan-line').filter({ hasText: 'Water' }) })
	).toHaveAttribute('data-date', today);
	await expect(ledger).not.toContainText('Rent');
	await expect(upcoming.last()).toHaveAttribute('data-date', shiftDays(start, 6));
	await expect(page.getByTestId('projected')).toContainText('Projected Savings Amount');
	await expect(page.getByTestId('projected')).toHaveClass(/bg-success/);
	await expect(receipt).toContainText('Left on Thursday');
	await expect(page.getByTestId('projected-math')).toContainText('at the start');
	await expect(page.getByTestId('projected-math')).toContainText('still to come');
	// under it, where savings will be once this week's projected savings moves
	await expect(page.getByTestId('projected-savings-balance')).toContainText(
		'Projected Amount in Savings'
	);
	await expect(page.getByTestId('left-on')).toHaveText(usd(left));
	await expect(page.getByTestId('review-line')).toContainText('to review');

	// Copy puts the number she writes down on the clipboard
	await hydrated(page);
	await page.getByTestId('copy-left').click();
	await expect(page.getByText(`Copied ${(left / 100).toFixed(2)}`)).toBeVisible();
	expect(await page.evaluate(() => navigator.clipboard.readText())).toBe((left / 100).toFixed(2));

	// a bill ticked by hand: the money is still in the account until the bank shows it, so it stays
	// still to come and the number she writes down does not move (it never jumps up)
	// the days still to come are folded up with their count and total; open today's to tick Water
	const todayDue = upcoming.and(page.locator(`[data-date="${today}"]`));
	await expect(todayDue).not.toHaveAttribute('open', '');
	await todayDue.locator('summary').click();
	await water.getByRole('button', { name: 'Mark Water paid' }).click();
	await expect(water.getByTestId('plan-status')).toHaveText('Paid · waiting for the bank');
	await expect(page.getByTestId('left-on')).toHaveText(usd(left));

	// a one-off from one sentence joins it
	await quickAdd(page, 'oil change 60 today');
	left -= 6000;
	await expect(upcoming.getByTestId('plan-line').filter({ hasText: /oil change/i })).toHaveCount(1);
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
