import { expect, test } from '@playwright/test';
import { addAccount, hydrated, registerWithHousehold } from './helpers';

/** v6: one Settings page; inline edits persist. */
test('one settings page: auto-match level, weekly target, theme, re-check matches', async ({
	page,
	context
}) => {
	await registerWithHousehold(page);
	await addAccount(page, 'Everyday', '500');
	await page.goto('/more');
	await page.getByRole('link', { name: /Settings/ }).click();
	await expect(page).toHaveURL(/\/settings$/);
	await hydrated(page);
	for (const s of ['You', 'Household', 'Banks'])
		await expect(page.getByRole('heading', { level: 2, name: s, exact: true })).toBeVisible();

	// auto-match level: Everything by default; Bills only saves on tap and survives a reload
	const level = page.getByRole('radiogroup', { name: 'Auto-match' });
	await expect(level.getByRole('radio', { name: /Everything/ })).toBeChecked();
	await level.getByRole('radio', { name: /Bills only/ }).check();
	await expect(page.getByText('Saved', { exact: true })).toBeVisible();
	await page.reload();
	await expect(
		page.getByRole('radiogroup', { name: 'Auto-match' }).getByRole('radio', { name: /Bills only/ })
	).toBeChecked();

	// weekly target
	await page.getByRole('textbox', { name: 'Weekly spending target' }).fill('250');
	await page.locator('#weekly').getByRole('button', { name: 'Save' }).click();
	await expect(page.getByText('Saved', { exact: true }).first()).toBeVisible();
	await page.reload();
	await expect(page.getByRole('textbox', { name: 'Weekly spending target' })).toHaveValue('250.00');
	await page.goto('/week');
	await expect(page.getByTestId('week-budget-overall')).toContainText('$250.00');

	// theme (You)
	await page.goto('/settings');
	await hydrated(page);
	await page.getByTestId('theme-dark').click();
	await expect(page.locator('html')).toHaveAttribute('data-theme', 'couplescash-dark');
	await expect
		.poll(async () => (await context.cookies()).find((c) => c.name === 'theme')?.value)
		.toBe('dark');
	await page.reload();
	await expect(page.locator('html')).toHaveAttribute('data-theme', 'couplescash-dark');

	// Banks: links + matcher status + re-check (rate-limited to once a minute)
	await expect(page.getByRole('link', { name: /Linked banks/ })).toHaveAttribute(
		'href',
		'/settings/banks'
	);
	await expect(page.getByRole('link', { name: /Import a CSV/ })).toHaveAttribute(
		'href',
		'/imports/csv'
	);
	await expect(page.getByTestId('matcher-status')).toContainText('Smart matching is on');
	await page.getByTestId('rematch').click();
	await expect(page.getByText('Re-checking everything in To review…')).toBeVisible();
	await page.getByTestId('rematch').click();
	await expect(page.getByText('Checked a moment ago. Try again in a minute.')).toBeVisible();
	// Partner row links to the Couple Code
	await expect(page.getByTestId('partner-line')).toContainText('Invite your partner');
});
