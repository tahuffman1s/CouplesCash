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
test('payday: Done by hand, then the bank ticks paycheck, fun money and the sweep', async ({
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
	const sweepText = await item('sweep').getByTestId('sweep-text').innerText();
	expect(sweepText).toMatch(/^Move \$[\d,]+\.\d{2} to savings$/);
	const amount = sweepText.replace(/^Move \$|,| to savings$/g, '');

	// Copy the amount for the bank app
	await hydrated(page);
	await item('sweep').getByTestId('copy-sweep').click();
	expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(amount);

	// the bank is slow: tick the paycheck by hand, then take it back (a sweep unticked by hand is
	// never re-ticked by the bank, so it is left alone here)
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
	await expect(done.locator('[data-key="sweep"]').getByTestId('checklist-auto')).toBeVisible();

	// the fun money in the plan is paid, and the transfers left To review
	const funRow = page.getByTestId('plan-row-fun');
	await funRow.locator('summary').click();
	for (const s of await funRow.getByTestId('plan-status').all())
		await expect(s).toContainText('Paid · auto');
	await expect(page.getByTestId('plan-amount-fun')).toHaveText(usd(0));

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
