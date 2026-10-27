import { expect, test, type Page } from '@playwright/test';
import { hydrated, registerWithHousehold } from './helpers';

/**
 * v7 Plaid: Settings → Banks → Connect a bank opens Plaid Link (stubbed here: the real script lives on cdn.plaid.com), whose
 * public token goes to the API; Reconnect opens Link in update mode; OAuth banks come back to /settings/banks/plaid-oauth.
 * The mock API knows `public-demo` (the demo bank) and `public-reauth` (a link that already wants a new sign-in).
 */
async function stubPlaid(page: Page, behaviour: 'success' | 'exit' = 'success') {
	await page.route('https://cdn.plaid.com/**', (route) =>
		route.fulfill({
			contentType: 'application/javascript',
			body: `window.Plaid = { create(opts) {
				window.__plaidCreated = (window.__plaidCreated || []).concat([{ token: opts.token, redirect: opts.receivedRedirectUri || null }]);
				return { open() { setTimeout(() => {
					${
						behaviour === 'exit'
							? "opts.onExit({ display_message: 'You closed Plaid before finishing.' });"
							: "opts.onSuccess(window.__plaidPublicToken || 'public-demo', { institution: { name: 'U.S. Bank' } });"
					}
				}, 30); } };
			} };`
		})
	);
}

async function plaidOffered(page: Page) {
	await page.goto('/settings/banks');
	await expect(page.getByRole('heading', { name: 'How bank sync works' })).toBeVisible();
	test.skip(
		(await page.getByTestId('plaid-card').count()) === 0,
		'Plaid is not configured on this API'
	);
}

test('connect a bank through Plaid, reconnect when the bank wants a new sign-in', async ({
	page
}) => {
	await registerWithHousehold(page);
	await stubPlaid(page);
	await plaidOffered(page);

	// SimpleFIN stays available below it
	await expect(page.getByText('Or link through SimpleFIN')).toBeVisible();
	await hydrated(page);
	await page.getByTestId('plaid-connect').click();
	await expect(page.getByText('Linked U.S. Bank · 2 accounts found')).toBeVisible();
	const card = page.getByTestId('bank-link').filter({ hasText: 'U.S. Bank' });
	await expect(card).toContainText('via Plaid');
	await expect(card).toContainText('Syncing every hour');
	await expect(card.getByTestId('plaid-reconnect')).toHaveCount(0);

	// a second bank that wants a new sign-in: Reconnect opens Link for that link and clears it
	await page.evaluate(
		() =>
			((window as unknown as { __plaidPublicToken: string }).__plaidPublicToken = 'public-reauth')
	);
	await page.getByTestId('plaid-connect').click();
	await expect(page.getByTestId('plaid-reconnect')).toBeVisible();
	await expect(page.getByText('The bank wants you to sign in again. Tap Reconnect.')).toBeVisible();
	await page.getByTestId('plaid-reconnect').click();
	await expect(page.getByText('Reconnected · syncing')).toBeVisible();
	await expect(page.getByTestId('plaid-reconnect')).toHaveCount(0);
	await expect(page.getByTestId('bank-link')).toHaveCount(2);
});

test('closing Plaid adds nothing; an OAuth bank comes back to the return page and finishes there', async ({
	page
}) => {
	await registerWithHousehold(page);
	await stubPlaid(page, 'exit');
	await plaidOffered(page);
	await hydrated(page);
	await page.getByTestId('plaid-connect').click();
	await expect(page.getByText('You closed Plaid before finishing.')).toBeVisible();
	await expect(page.getByTestId('bank-link')).toHaveCount(0);

	// nothing started here: the return page goes straight back
	await page.goto('/settings/banks/plaid-oauth?oauth_state_id=abc');
	await expect(page).toHaveURL(/\/settings\/banks$/);

	// a Link flow saved before leaving for the bank: Link re-opens with the URL it came back to
	await page.unroute('https://cdn.plaid.com/**');
	await stubPlaid(page);
	await page.evaluate(() =>
		sessionStorage.setItem(
			'cc-plaid-link',
			JSON.stringify({ token: 'link-sandbox-saved', linkId: null })
		)
	);
	await page.goto('/settings/banks/plaid-oauth?oauth_state_id=abc');
	await expect(page).toHaveURL(/\/settings\/banks$/);
	await expect(page.getByText('Linked U.S. Bank · 2 accounts found')).toBeVisible();
	await expect(page.getByTestId('bank-link')).toHaveCount(1);
	expect(await page.evaluate(() => sessionStorage.getItem('cc-plaid-link'))).toBeNull();
});
