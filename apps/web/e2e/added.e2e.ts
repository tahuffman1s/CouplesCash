import { expect, test } from '@playwright/test';
import {
	addBill,
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
	// … and it is a line of today in the ledger, marked as added by you
	await expect(
		page.getByTestId('ledger-day').last().getByTestId('ledger-entry').filter({ hasText: 'Coffee' })
	).toContainText('added by you');

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

/** "$2,345.67" / "−$60.00" → cents */
const cents = (text: string | null) =>
	Math.round(Number((text ?? '').replace(/[^0-9.]/g, '')) * 100) *
	(/^[−-]/.test(text?.trim() ?? '') ? -1 : 1);

test('a transaction added by hand can pay a bill, so it is counted once', async ({ page }) => {
	await registerWithHousehold(page, 'Travis');
	await skipUnlessMockBridge(page);
	const today = todayIso();
	await addBill(page, { name: 'Water', amount: '61.37', firstDue: today });
	await setupHousehold(page, { payday: weekdayOf(today) });
	const left = page.getByTestId('left-on');
	const stillToCome = () =>
		page.getByTestId('ledger-upcoming').getByTestId('plan-line').filter({ hasText: 'Water' });
	await expect(stillToCome()).toHaveCount(1);
	const before = cents(await left.textContent());

	// paid this morning, added by hand: until it is linked the bill is counted too
	await addTransaction(page, { amount: '-61.37', description: 'City water' });
	await expect(left).toHaveText(usd(before - 6137));

	// link it from its sheet: the bill is paid by it, the plan counts it once
	await page.goto('/transactions');
	const row = () =>
		page.getByTestId('pending-section').getByTestId('tx-row').filter({ hasText: 'City water' });
	await row().click();
	const sheet = page.getByTestId('transaction-sheet');
	await expect(sheet.getByTestId('sheet-state')).toHaveText('Added by you · waiting for the bank');
	await sheet.getByTestId('match-candidate').filter({ hasText: 'Water' }).click();
	await expect(page.getByText('Matched to Water')).toBeVisible();
	await expect(row()).toContainText('Water · added');
	await page.goto('/week');
	await expect(left).toHaveText(usd(before));
	// today's ledger shows it once, as the bill it paid; it is no longer still to come
	const todays = page.getByTestId('ledger-day').last().getByTestId('ledger-entry');
	await expect(todays.filter({ hasText: 'City water' })).toHaveCount(0);
	await expect(todays.filter({ hasText: 'Water' })).toContainText('Bill paid');
	await expect(todays.filter({ hasText: 'Water' }).getByTestId('ledger-entry-amount')).toHaveText(
		'−$61.37'
	);
	await expect(stillToCome()).toHaveCount(0);

	// unlink: the bill is unpaid again (and counted again)
	await page.goto('/transactions');
	await row().click();
	await expect(sheet.getByTestId('sheet-state')).toContainText('Paid Water');
	await sheet.getByTestId('unlink-added').click();
	await expect(page.getByText('Unlinked from Water')).toBeVisible();
	await page.goto('/week');
	await expect(left).toHaveText(usd(before - 6137));

	// link it again, then delete it: the bill goes back to unpaid
	await page.goto('/transactions');
	await row().click();
	await sheet.getByTestId('match-candidate').filter({ hasText: 'Water' }).click();
	await expect(page.getByText('Matched to Water')).toBeVisible();
	await row().click();
	await sheet.getByTestId('delete-added').click();
	await expect(page.getByText('Removed City water')).toBeVisible();
	await page.goto('/week');
	await expect(left).toHaveText(usd(before));
	await expect(stillToCome()).toHaveCount(1);
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
