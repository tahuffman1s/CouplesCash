import { expect, type Page } from '@playwright/test';

let seq = 0;
export function uniqueEmail(prefix = 'user') {
	seq += 1;
	return `${prefix}-${Date.now()}-${process.pid}-${seq}@example.com`;
}

export const PASSWORD = 'correct-horse-battery';

/** Registers a brand-new user and lands on the dashboard. */
export async function register(page: Page, name = 'Alex') {
	const email = uniqueEmail();
	await page.goto('/register');
	await page.getByLabel('Your name').fill(name);
	await page.getByLabel('Email').fill(email);
	await page.getByLabel('Password', { exact: true }).fill(PASSWORD);
	await page.getByRole('button', { name: 'Create account' }).click();
	await expect(page).toHaveURL(/\/$/);
	return { email, name };
}

/** Registers and creates a household (via the couple code flow) so household routes work. */
export async function registerWithHousehold(page: Page, name = 'Alex') {
	const creds = await register(page, name);
	await page.goto('/couple');
	await page.getByLabel('Name your household').fill('Test House');
	await page.getByRole('button', { name: /Create household/ }).click();
	await expect(page.getByTestId('couple-code')).toBeVisible();
	return creds;
}

export function todayIso(): string {
	const d = new Date();
	const pad = (n: number) => (n < 10 ? `0${n}` : `${n}`);
	return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

export const MOCK_API = `http://127.0.0.1:${process.env.MOCK_API_PORT ?? 8099}`;

/** Adds a checking account through the UI and returns to the caller on its detail page. */
export async function addAccount(page: Page, name: string, balance: string, type = 'Checking') {
	await page.goto('/accounts/new');
	await page.getByLabel('Account name').fill(name);
	await page.getByLabel('Bank or institution').fill('Bank');
	await page
		.getByRole('radiogroup', { name: 'Account type' })
		.getByText(type, { exact: true })
		.click();
	await page.getByLabel(/Current balance|Amount owed/).fill(balance);
	await page.getByRole('button', { name: 'Save account' }).click();
	await expect(page).toHaveURL(/\/accounts\/[0-9a-f-]+/);
}
