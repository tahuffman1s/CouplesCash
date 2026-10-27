import { expect, test } from '@playwright/test';
import { hydrated, registerWithHousehold, skipUnlessMockBridge } from './helpers';

/** v7 first run: the guided setup replaces the plan until there is a bills account. */
test('setup wizard: bank → roles → payday → fun money → notes → invite → the plan', async ({
	page
}) => {
	await registerWithHousehold(page, 'Travis');
	await skipUnlessMockBridge(page);

	await page.goto('/week');
	const overview = page.getByTestId('setup-overview');
	await expect(overview).toContainText("Let's set up your Friday");
	await expect(overview.getByTestId('setup-step-link')).toHaveCount(6);
	await expect(page.getByTestId('plan-receipt')).toHaveCount(0);
	await overview.getByTestId('setup-start').click();
	await expect(page).toHaveURL(/\/setup\?step=banks$/);

	// 1. link the bank inline
	await page.getByLabel('Setup token').fill('demo-household');
	await page.getByRole('button', { name: 'Link bank' }).click();
	// 2. straight on to roles, defaulted from the bank's names
	await expect(page).toHaveURL(/step=roles/);
	await expect(page.getByText('Linked Our banks · 4 accounts found')).toBeVisible();
	await hydrated(page);
	const role = (name: string) => page.locator(`[data-testid="role-row"][data-name="${name}"]`);
	await expect(page.getByTestId('role-row')).toHaveCount(4);
	const checked = (name: string, r: string) =>
		expect(role(name).getByRole('radio', { name: r })).toHaveAttribute('aria-checked', 'true');
	await checked('Easy Checking', 'Bills');
	await checked('Online Savings', 'Savings');
	await checked('Spending Account ··4411', 'Fun money');
	await checked('Spending Account ··4412', 'Fun money');
	await expect(
		role('Spending Account ··4411')
			.getByLabel(/Whose fun money/)
			.locator('option:checked')
	).toHaveText('Travis');
	// only one bills account: picking another moves it
	await role('Online Savings').getByRole('radio', { name: 'Bills' }).click();
	await checked('Easy Checking', 'Other');
	await role('Easy Checking').getByRole('radio', { name: 'Bills' }).click();
	await role('Online Savings').getByRole('radio', { name: 'Savings' }).click();
	await page.getByTestId('setup-save').click();

	// 3. payday: Friday by default, the paycheck found in the bank
	await expect(page).toHaveURL(/step=payday/);
	await expect(page.getByLabel('Friday')).toBeChecked();
	await expect(page.getByTestId('payweek-hint')).toHaveText('Your week runs Friday → Thursday.');
	await expect(page.getByLabel('Paycheck amount')).toHaveValue('2450.00');
	await expect(page.getByTestId('paycheck-found')).toContainText('ACME CORP PAYROLL +$2,450.00');
	await page.getByTestId('setup-save').click();

	// 4. fun money prefilled: $20 every Friday, $150 at the start of the month, each
	await expect(page).toHaveURL(/step=fun/);
	await expect(page.getByTestId('fun-row')).toHaveCount(2);
	await expect(page.getByLabel('Weekly fun money for Travis')).toHaveValue('20');
	await expect(page.getByLabel('Monthly fun money for Travis')).toHaveValue('150');
	await page.getByTestId('setup-save').click();

	// 5. paste her notes: five bills, one line it can't read
	await expect(page).toHaveURL(/step=notes/);
	await hydrated(page);
	await page
		.getByTestId('notes-input')
		.fill(
			[
				'- Rent 1850 1st',
				'- Netflix 15.49 7th',
				'- Water ~60 20th',
				'- call the bank about the card',
				'- Phone 85 12th',
				'- Student loan 312 15th'
			].join('\n')
		);
	await expect(page.getByTestId('note-row')).toHaveCount(5);
	await expect(page.getByTestId('note-skipped')).toHaveCount(1);
	await expect(page.getByTestId('note-skipped')).toContainText('call the bank about the card');
	await expect(page.getByTestId('note-skipped')).toContainText('No amount');
	const loan = page.getByTestId('note-row').filter({ hasText: 'Student loan' });
	await expect(loan.getByTestId('note-account')).toHaveText('Savings');
	await expect(
		page.getByTestId('note-row').filter({ hasText: 'Water' }).getByTestId('note-amount')
	).toHaveText('~$60.00');
	await page.getByTestId('notes-submit').click();

	// 6. invite
	await expect(page).toHaveURL(/step=partner&added=5/);
	await expect(page.getByTestId('notes-added')).toHaveText(/5 bills added from your notes/);
	await page.getByTestId('invite').click();
	await expect(page).toHaveURL(/\/couple/);
	await expect(page.getByTestId('couple-code')).toBeVisible();

	// the plan replaces the setup; the bills are there
	await page.goto('/week');
	await expect(page.getByTestId('setup-overview')).toHaveCount(0);
	await expect(page.getByTestId('plan-receipt')).toContainText('Available now');
	await expect(page.getByTestId('strip-account')).toHaveCount(4);
	await expect(page.getByTestId('strip-account').first()).toContainText('U.S. Bank');
	await expect(page.getByTestId('accounts-strip')).toContainText('Savings');
	await expect(page.getByTestId('accounts-strip')).toContainText('Fun (Travis)');
	await page.goto('/bills');
	await expect(page.getByTestId('bill-row')).toHaveCount(5 + 1 + 4);
	await expect(
		page.getByTestId('bill-row').filter({ hasText: 'Student loan' }).getByTestId('bill-account')
	).toHaveText('Savings');
	await page.getByRole('radio', { name: 'Transfers' }).click();
	await expect(page.getByTestId('bill-row')).toHaveCount(4);
	const funTravis = page.getByTestId('bill-row').filter({ hasText: 'Fun money · Travis' }).first();
	await expect(funTravis.getByTestId('bill-account')).toHaveText('→ Fun (Travis)');
	await funTravis.click();
	await expect(page.getByTestId('transfer-route')).toHaveText('U.S. Bank → Fun (Travis)');

	// every step can be revisited from Settings
	await page.goto('/settings');
	await page.getByTestId('rerun-setup').click();
	await expect(page).toHaveURL(/\/setup$/);
	await expect(page.getByTestId('setup-step')).toBeVisible();
});
