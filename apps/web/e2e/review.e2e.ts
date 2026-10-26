import { expect, test } from '@playwright/test';
import {
	addAccount,
	addBill,
	hydrated,
	linkDemoBank,
	mapChecking,
	registerWithHousehold,
	reviewRow,
	shiftDays,
	skipUnlessMockBridge,
	swipe,
	todayIso,
	txRow
} from './helpers';

/** v6 To review: one big button that says the suggestion, swipes, Undo, "Do all", All caught up. */
test('To review: "It\'s Rent", swipe to ignore + Undo, swipe to accept, do all, all caught up', async ({
	page
}) => {
	await registerWithHousehold(page);
	await addAccount(page, 'Everyday', '1000');
	await skipUnlessMockBridge(page);
	const today = todayIso();
	await addBill(page, { name: 'Rent', amount: '1850', firstDue: `${today.slice(0, 7)}-01` });
	await addBill(page, {
		kind: 'Income',
		name: 'Paycheck',
		amount: '3200',
		firstDue: shiftDays(today, -2)
	});
	await linkDemoBank(page);
	await mapChecking(page);

	await page.goto('/transactions');
	await hydrated(page);
	await expect(page.getByTestId('review-row')).toHaveCount(5);

	// one tap on the big button
	await reviewRow(page, 'RENT PAYMENT').getByRole('button', { name: "It's Rent" }).click();
	await expect(page.getByText('Rent marked paid')).toBeVisible();
	await expect(page.getByTestId('review-row')).toHaveCount(4);

	// swipe left = Ignore, with Undo
	const netflix = reviewRow(page, 'NETFLIX.COM');
	await swipe(page, netflix.getByTestId('review-swipe'), -160);
	await expect(page.getByText('Ignored NETFLIX.COM')).toBeVisible();
	await expect(netflix).toHaveCount(0);
	await expect(txRow(page, 'NETFLIX.COM').getByTestId('tx-status')).toHaveText('Ignored');
	await expect(page.getByTestId('review-badge')).toHaveText('3');
	await page
		.getByTestId('toast')
		.filter({ hasText: 'Ignored NETFLIX.COM' })
		.getByRole('button', { name: 'Undo' })
		.click();
	await expect(page.getByText('Back in To review')).toBeVisible();
	await expect(reviewRow(page, 'NETFLIX.COM')).toHaveCount(1);
	await expect(page.getByTestId('review-badge')).toHaveText('4');

	// swipe right = the big button (Kroger → Groceries)
	await swipe(page, reviewRow(page, 'KROGER #123').getByTestId('review-swipe'), 160);
	await expect(page.getByText('Expense added in Groceries')).toBeVisible();
	await expect(txRow(page, 'KROGER #123').getByTestId('tx-status')).toHaveText('Groceries');

	// "Do all suggestions" takes the confident ones (the paycheck)
	await page.getByTestId('do-all').click();
	await expect(page.getByText(/1 done/)).toBeVisible();
	await expect(page.getByTestId('review-row')).toHaveCount(2);

	// the last two by tapping, then the list says so and the badge is gone
	await reviewRow(page, 'COFFEE').getByTestId('review-primary').click();
	await expect(page.getByText('Expense added in Fun')).toBeVisible();
	await swipe(page, reviewRow(page, 'NETFLIX.COM').getByTestId('review-swipe'), -160);
	await expect(page.getByTestId('all-caught-up')).toBeVisible();
	await expect(page.getByTestId('review-badge')).toHaveCount(0);
	await expect(page.getByTestId('do-all')).toHaveCount(0);

	// …and the plan has no "to review" line
	await page.goto('/week');
	await expect(page.getByTestId('review-line')).toHaveCount(0);
});

test('account chips and search filter the feed; auto-matched rows say why', async ({ page }) => {
	await registerWithHousehold(page);
	await addAccount(page, 'Everyday', '1000');
	await skipUnlessMockBridge(page);
	await linkDemoBank(page, 'demo-v5');
	await mapChecking(page);
	await page.getByLabel('Mapped account for Demo Bank · Way2Save Savings').selectOption('__create');
	await page.getByLabel('New account name').fill('Savings');
	await page.getByRole('button', { name: 'Create & map' }).click();
	await expect(page.getByText('Way2Save Savings now updates Savings')).toBeVisible();

	await page.goto('/transactions');
	const chips = page.getByRole('radiogroup', { name: 'Account' });
	await chips.getByRole('radio', { name: 'Savings' }).click();
	await expect(page).toHaveURL(/account=/);
	await expect(page.getByTestId('review-row')).toHaveCount(0);
	await expect(txRow(page, 'ONLINE TRANSFER FROM CHK')).toHaveCount(1);
	await expect(txRow(page, 'KROGER')).toHaveCount(0);
	// auto-ignored transfers carry the auto mark
	await expect(txRow(page, 'ONLINE TRANSFER FROM CHK').getByTestId('tx-auto')).toBeVisible();

	await chips.getByRole('radio', { name: 'All accounts' }).click();
	await page.getByLabel('Search transactions').fill('kroger');
	await expect(page).toHaveURL(/q=kroger/);
	await expect(page.getByTestId('review-row')).toHaveCount(1);
	await expect(reviewRow(page, 'KROGER #456')).toBeVisible();
	await page.getByLabel('Search transactions').fill('nothing like this');
	await expect(page.getByTestId('tx-empty')).toBeVisible();
});
