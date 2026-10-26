import { expect, test } from '@playwright/test';
import { addAccount, hydrated, quickAdd, register, registerWithHousehold } from './helpers';

/** v6: Week is home, the first-run checklist, the five tabs and redirects for every old URL. */

test('first run: the week shows three steps that tick themselves off', async ({ browser }) => {
	const a = await browser.newContext();
	const page = await a.newPage();
	await registerWithHousehold(page, 'Riley');
	const code = await page.getByTestId('couple-code').innerText();

	// an empty household sees the checklist instead of zeros
	await page.goto('/week');
	const steps = page.getByTestId('first-run');
	await expect(steps).toBeVisible();
	await expect(page.getByTestId('first-run-progress')).toHaveText('0 of 3 done');
	await expect(page.getByTestId('left-at-end')).toHaveCount(0);

	// 1 · accounts
	await steps.getByTestId('step-accounts').click();
	await expect(page).toHaveURL(/\/accounts\/new$/);
	await addAccount(page, 'Everyday', '1200');
	await page.goto('/week');
	await expect(page.getByTestId('step-accounts')).toHaveAttribute('data-done', 'true');
	await expect(page.getByTestId('first-run-progress')).toHaveText('1 of 3 done');
	await expect(page.getByTestId('left-at-end')).toHaveText('$1,200.00');

	// 2 · bills: the step opens the one-sentence sheet
	await page.getByTestId('step-bills').click();
	await expect(page.getByTestId('quick-add-form')).toBeVisible();
	await page.keyboard.press('Escape');
	await quickAdd(page, 'rent 1850 1st autopay');
	await expect(page.getByTestId('step-bills')).toHaveAttribute('data-done', 'true');
	await expect(page.getByTestId('first-run-progress')).toHaveText('2 of 3 done');

	// 3 · partner joins with the Couple Code → the checklist disappears
	const b = await browser.newContext();
	const partner = await b.newPage();
	await register(partner, 'Jordan');
	await partner.goto(`/couple/join?c=${encodeURIComponent(code)}`);
	await partner.getByRole('button', { name: 'Join household' }).click();
	await expect(partner).toHaveURL(/\/week\?joined=1$/);
	await expect(partner.getByTestId('first-run')).toHaveCount(0);

	await page.reload();
	await expect(page.getByTestId('first-run')).toHaveCount(0);
	await expect(page.getByTestId('left-at-end')).toBeVisible();
	await Promise.all([a.close(), b.close()]);
});

test('five tabs with one job each; More has four rows', async ({ page }) => {
	await registerWithHousehold(page);
	const dock = page.getByRole('navigation', { name: 'Primary' });
	for (const [tab, url, title] of [
		['Transactions', /\/transactions$/, 'Transactions'],
		['Bills', /\/bills$/, 'Bills'],
		['Money', /\/accounts$/, 'Money'],
		['More', /\/more$/, 'More'],
		['Week', /\/week$/, 'This week']
	] as const) {
		await dock.getByRole('link', { name: tab }).click();
		await expect(page).toHaveURL(url);
		await expect(page.getByRole('heading', { level: 1, name: title })).toBeVisible();
		await expect(dock.getByRole('link', { name: tab })).toHaveAttribute('aria-current', 'page');
	}
	await page.goto('/more');
	const rows = page.locator('main ul > li');
	await expect(rows).toHaveCount(4);
	await expect(rows).toHaveText([/Insights/, /To pay/, /Activity/, /Settings/]);
	// one + in the app bar of Week, Bills and Transactions
	for (const path of ['/week', '/bills', '/transactions']) {
		await page.goto(path);
		await expect(page.getByTestId('quick-add')).toHaveCount(1);
	}
	// the PWA starts on the week
	const manifest = await (await page.request.get('/manifest.webmanifest')).json();
	expect(manifest.start_url).toBe('/week');
});

test('old URLs land on their new home', async ({ page }) => {
	await registerWithHousehold(page);
	for (const [from, to] of [
		['/', /\/week$/],
		['/inbox', /\/transactions$/],
		['/inbox?status=confirmed', /\/transactions$/],
		['/calendar?m=2026-01', /\/bills\?view=calendar&m=2026-01$/],
		['/payperiod', /\/week$/],
		['/pending', /\/accounts#pending$/],
		['/goals', /\/accounts#goals$/],
		['/settings/household', /\/settings#household$/],
		['/settings/notifications', /\/settings#you$/],
		['/bills?tab=expenses', /\/transactions$/]
	] as const) {
		await page.goto(from);
		await expect(page, `${from} redirects`).toHaveURL(to);
	}
	await expect(page.getByRole('heading', { level: 1, name: 'Transactions' })).toBeVisible();
	await page.goto('/calendar?m=2026-01');
	await expect(page.getByTestId('month-label')).toHaveText(/January 2026/);
});

test('helper text under each title can be dismissed and stays dismissed on this device', async ({
	page
}) => {
	await registerWithHousehold(page);
	await page.goto('/week');
	const help = page.getByTestId('helper-text');
	await expect(help).toContainText("How much is left this week, and what's coming.");
	await help.getByRole('button', { name: /hide this tip/ }).click();
	await expect(help).toHaveCount(0);
	await page.reload();
	await hydrated(page);
	await expect(page.getByTestId('helper-text')).toHaveCount(0);
	// each screen has its own
	await page.goto('/bills');
	await expect(page.getByTestId('helper-text')).toContainText('Everything that repeats');
	await page.goto('/transactions');
	await expect(page.getByTestId('helper-text')).toContainText('Everything that hit your accounts');
	await page.goto('/accounts');
	await expect(page.getByTestId('helper-text')).toContainText('Every account');
});
