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
