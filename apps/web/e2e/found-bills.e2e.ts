import { expect, test } from '@playwright/test';
import {
	addAccount,
	bankSync,
	linkDemoBank,
	mapChecking,
	registerWithHousehold,
	reviewRow,
	skipUnlessMockBridge,
	txRow
} from './helpers';

/** v6 found bills: regular payments spotted in imported transactions → Add / Not a bill. */
test('found bills: Add matches the past payments, Not a bill hides it for good', async ({
	page
}) => {
	const { email } = await registerWithHousehold(page);
	await addAccount(page, 'Everyday', '1000');
	await skipUnlessMockBridge(page);
	await linkDemoBank(page, 'demo-v5');
	await mapChecking(page);
	await bankSync(page, email, 'netflix');

	await page.goto('/week');
	const card = page.getByTestId('found-bills');
	await expect(card).toContainText('We found 2 regular payments');
	const netflix = card.getByTestId('found-bill').filter({ hasText: 'Netflix' });
	await expect(netflix).toContainText('−$15.49');
	await expect(netflix).toContainText('every month · next');

	// Add: the bill is created with the bank's name and the three past charges match it
	await netflix.getByRole('button', { name: 'Add Netflix' }).click();
	await expect(page.getByText('Netflix added · 3 past payments matched')).toBeVisible();
	await expect(card.getByTestId('found-bill')).toHaveCount(1);

	// Not a bill: gone, and a later sync does not bring it back
	await page.goto('/bills');
	const spotify = page.getByTestId('found-bill').filter({ hasText: 'Spotify' });
	await expect(spotify).toBeVisible();
	await spotify.getByRole('button', { name: 'Spotify is not a bill' }).click();
	await expect(page.getByText('Got it, Spotify is not a bill')).toBeVisible();
	await expect(page.getByTestId('found-bills')).toHaveCount(0);
	await bankSync(page, email, 'netflix');
	await page.reload();
	await expect(page.getByTestId('found-bills')).toHaveCount(0);

	// the Netflix rows are matched, not waiting for review
	await page.getByRole('radio', { name: 'Subscriptions' }).click();
	await expect(page.getByTestId('bill-list').getByRole('link', { name: /Netflix/ })).toBeVisible();
	await page.goto('/transactions');
	await expect(reviewRow(page, 'NETFLIX.COM')).toHaveCount(0);
	await expect(txRow(page, 'NETFLIX.COM')).toHaveCount(3);
	await expect(txRow(page, 'NETFLIX.COM').first().getByTestId('tx-status')).toHaveText('Netflix');
});
