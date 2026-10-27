import { expect, test } from '@playwright/test';
import {
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
 * v7 payday checklist: payday is set to today so the checklist shows; `?phase=payday` brings the
 * paycheck, the fun-money transfers and the sweep of last week's leftover into the bank feed.
 */
test('payday: Done by hand, then the bank ticks the paycheck and fun money', async ({
	page,
	context
}) => {
	await context.grantPermissions(['clipboard-read', 'clipboard-write']);
	const { email } = await registerWithHousehold(page, 'Travis');
	await skipUnlessMockBridge(page);
	await setupHousehold(page, { payday: weekdayOf(todayIso()) });

	const list = page.getByTestId('payday-checklist');
	await expect(list).toContainText("It's payday");
	const item = (key: string) => list.locator(`[data-testid="checklist-item"][data-key="${key}"]`);
	await expect(item('paycheck')).toContainText('Paycheck arrived');
	await expect(item('paycheck')).toContainText('$2,450.00');
	await expect(item('fun').filter({ hasText: 'Travis' }).first()).toContainText('$20.00');
	// the move to savings is not on payday: it is the bottom line on the last day of the week
	await expect(item('sweep')).toHaveCount(0);
	await expect(page.getByTestId('sweep-line')).toHaveAttribute('data-state', 'later');
	await expect(page.getByTestId('sweep-text')).toContainText(
		/On \w+day, move what's left to savings/
	);

	// the bank is slow: tick the paycheck by hand, then take it back
	await hydrated(page);
	await item('paycheck').getByTestId('checklist-done').click();
	await expect(item('paycheck')).toHaveAttribute('data-done', 'true');
	await expect(list.getByTestId('checklist-progress')).toContainText('1 of');
	await item('paycheck').getByTestId('checklist-undo').click();
	await expect(item('paycheck')).toHaveAttribute('data-done', 'false');

	// the bank sends every fun transfer planned this pay week (the $150s too in a week with a 1st)
	const funCount = await page.getByTestId('plan-row-fun').getByTestId('plan-line').count();
	// the payday transactions arrive: every line ticks itself ("auto"), the card folds away
	await bankSync(page, email, 'payday');
	await page.reload();
	const done = page.getByTestId('payday-done');
	await expect(done).toContainText('Payday done ✓');
	await expect(page.getByTestId('payday-checklist')).toHaveCount(0);
	await done.locator('summary').click();
	for (const row of await done.getByTestId('checklist-item').all())
		await expect(row).toHaveAttribute('data-done', 'true');
	await expect(done.getByTestId('checklist-auto').first()).toBeVisible();

	// the fun money in the plan is paid, and the transfers left To review
	const funRow = page.getByTestId('plan-row-fun');
	await funRow.locator('summary').click();
	for (const s of await funRow.getByTestId('plan-status').all())
		await expect(s).toContainText('Paid · auto');
	// … and still counted in the plan (every line of the week counts), added back as already paid
	const funText = (await page.getByTestId('plan-amount-fun').textContent())!.trim();
	expect(funText.startsWith('−')).toBe(true);
	await expect(page.getByTestId('plan-amount-paid')).toHaveText(funText.replace('−', '+'));

	// every pending row is listed: the 3 card holds plus today's still-pending fun transfers,
	// in the plan (the "after N pending" line and its list) and in Transactions → Pending
	const pending = 3 + funCount;
	await expect(page.getByTestId('pending-subline')).toContainText(`after ${pending} pending`);
	await page.getByTestId('plan-row-available').locator('summary').click();
	await expect(page.getByTestId('plan-pending').locator('li')).toHaveCount(pending);
	await expect(page.getByTestId('plan-pending')).toContainText('ONLINE TRANSFER TO ALLY');
	await page.goto('/transactions');
	await expect(page.getByTestId('pending-count')).toHaveText(String(pending));
	await expect(page.getByTestId('pending-section').getByTestId('tx-row')).toHaveCount(pending);
	// the other legs land on the fun accounts: listed once fun money is shown
	await page.getByTestId('fun-toggle').click();
	await expect(page.getByTestId('pending-count')).toHaveText(String(pending + funCount));
});

test('the last day of the week: what is left moves to savings, and the bank ticks it', async ({
	page,
	context
}) => {
	await context.grantPermissions(['clipboard-read', 'clipboard-write']);
	const { email } = await registerWithHousehold(page, 'Travis');
	await skipUnlessMockBridge(page);
	// payday tomorrow: today is the last day of the pay week (her Thursday)
	await setupHousehold(page, { payday: (weekdayOf(todayIso()) + 1) % 7 });

	const left = await page.getByTestId('left-on').innerText();
	const line = page.getByTestId('sweep-line');
	await expect(line).toHaveAttribute('data-state', 'due');
	await expect(line.getByTestId('sweep-text')).toHaveText(`Move ${left} to savings today`);
	await expect(page.getByTestId('payday-checklist')).toHaveCount(0);

	// Copy the number for the bank app
	await hydrated(page);
	await line.getByTestId('copy-sweep').click();
	expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(left.replace(/[$,]/g, ''));

	// the transfer shows up at the bank: it ticks itself, and nothing is left on the last day
	await bankSync(page, email, 'sweep');
	await page.reload();
	await expect(line).toHaveAttribute('data-state', 'done');
	await expect(line.getByTestId('sweep-text')).toContainText(`Moved ${left} to savings ✓`);
	await expect(line.getByTestId('sweep-auto')).toBeVisible();
	await expect(page.getByTestId('left-on')).toHaveText(usd(0));
});
