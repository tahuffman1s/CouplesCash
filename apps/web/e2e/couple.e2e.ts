import { expect, test } from '@playwright/test';
import { register, registerWithHousehold } from './helpers';

test('couple page shows a QR and a typed code with a countdown', async ({ page }) => {
	await registerWithHousehold(page, 'Riley');
	const code = await page.getByTestId('couple-code').innerText();
	expect(code).toMatch(/^[0-9A-Z]{4}-[0-9A-Z]{4}$/);
	await expect(page.getByRole('img', { name: 'Couple Code QR' }).locator('svg')).toBeVisible();
	await expect(page.getByTestId('countdown')).toContainText(/Expires in \d+:\d{2}/);
	await page.getByRole('button', { name: 'Generate new code' }).click();
	await expect(page.getByTestId('couple-code')).not.toHaveText(code);
});

test('typed code join links two accounts', async ({ browser }) => {
	const a = await browser.newContext();
	const pageA = await a.newPage();
	await registerWithHousehold(pageA, 'Riley');
	const code = await pageA.getByTestId('couple-code').innerText();

	const b = await browser.newContext();
	const pageB = await b.newPage();
	await register(pageB, 'Jordan');
	await pageB.getByRole('link', { name: 'Set up our household' }).click();
	await pageB.getByRole('button', { name: 'Type a code' }).click();
	await pageB.getByLabel("Partner's Couple Code").fill(code.toLowerCase());
	await pageB.getByRole('button', { name: 'Join household' }).click();

	// joining lands on the Week; the couple page names the household
	await expect(pageB).toHaveURL(/\/week\?joined=1$/);
	await expect(pageB.getByText('Welcome to the household')).toBeVisible();
	await pageB.goto('/couple');
	await expect(pageB.getByRole('heading', { name: 'Test House' })).toBeVisible();
	await expect(pageB.getByText(/Linked since/)).toBeVisible();

	// the other side sees the partner after a reload
	await pageA.reload();
	await expect(pageA.getByText(/Riley/)).toBeVisible();
	await expect(pageA.getByText(/Jordan/)).toBeVisible();

	// a used code is rejected
	const c = await browser.newContext();
	const pageC = await c.newPage();
	await register(pageC, 'Casey');
	await pageC.goto(`/couple/join?c=${encodeURIComponent(code)}`);
	await pageC.getByRole('button', { name: 'Join household' }).click();
	await expect(pageC.getByRole('alert')).toContainText(/invalid, expired or already used/);

	await Promise.all([a.close(), b.close(), c.close()]);
});
