import { expect, test } from '@playwright/test';
import { addAccount, registerWithHousehold } from './helpers';

test('to-pay checklist: add, done/undone, reorder', async ({ page }) => {
	await registerWithHousehold(page, 'Kaylee');
	await addAccount(page, 'Everyday', '100');
	// More › To pay
	await page.goto('/more');
	await page.getByRole('link', { name: /To pay/ }).click();
	await expect(page).toHaveURL(/\/todos$/);
	await expect(page.getByText('Nothing on the list')).toBeVisible();

	await page.getByLabel('Title').fill('Transfer to savings');
	await page.getByLabel('Amount (optional)').fill('200');
	await page.getByRole('button', { name: 'Add', exact: true }).click();
	await expect(page.getByText('"Transfer to savings" added')).toBeVisible();
	await page.getByLabel('Title').fill('Call insurance');
	await page.getByRole('button', { name: 'Add', exact: true }).click();
	await expect(page.getByText('"Call insurance" added')).toBeVisible();

	const open = page.getByTestId('open-todos').getByTestId('todo-row');
	await expect(open).toHaveCount(2);
	await expect(open.nth(0)).toContainText('Transfer to savings');
	await expect(open.nth(0)).toContainText('$200.00');

	// reorder with the mobile-friendly buttons (PUT /todos/order)
	await page.getByRole('button', { name: 'Move Transfer to savings down' }).click();
	await expect(open.nth(0)).toContainText('Call insurance');
	await page.reload();
	await expect(page.getByTestId('open-todos').getByTestId('todo-row').nth(0)).toContainText(
		'Call insurance'
	);

	// tick one off (optimistic; wait for the API before reloading)
	const doneSaved = page.waitForResponse(
		(r) => /\/todos\/[^/]+\/done$/.test(r.url()) && r.request().method() === 'POST' && r.ok()
	);
	await page
		.getByTestId('open-todos')
		.getByRole('checkbox', { name: 'Mark "Call insurance" done' })
		.click();
	await doneSaved;

	// done items are hidden by default and shown with the toggle; undone brings it back
	await page.reload();
	await expect(page.getByTestId('open-todos').getByTestId('todo-row')).toHaveCount(1);
	await page.getByTestId('toggle-done').click();
	await expect(page).toHaveURL(/done=1/);
	const done = page.getByTestId('done-todos').getByTestId('todo-row');
	await expect(done).toHaveCount(1);
	await expect(done.first()).toContainText(/done by Kaylee/);
	await done.first().getByRole('checkbox').click();
	await expect(page.getByTestId('open-todos').getByTestId('todo-row')).toHaveCount(2);
});
