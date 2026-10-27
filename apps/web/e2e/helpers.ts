import { expect, test, type Page } from '@playwright/test';

let seq = 0;
export function uniqueEmail(prefix = 'user') {
	seq += 1;
	return `${prefix}-${Date.now()}-${process.pid}-${seq}@example.com`;
}

export const PASSWORD = 'correct-horse-battery';

/** Registers a brand-new user and lands on the Week (v6 home). */
export async function register(page: Page, name = 'Alex', email = uniqueEmail()) {
	await page.goto('/register');
	await page.getByLabel('Your name').fill(name);
	await page.getByLabel('Email').fill(email);
	await page.getByLabel('Password', { exact: true }).fill(PASSWORD);
	await page.getByRole('button', { name: 'Create account' }).click();
	await expect(page).toHaveURL(/\/week$/);
	return { email, name };
}

/** Registers and creates a household (via the couple code flow) so household routes work. */
export async function registerWithHousehold(page: Page, name = 'Alex', email = uniqueEmail()) {
	const creds = await register(page, name, email);
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

export function shiftDays(iso: string, n: number): string {
	const [y, m, d] = iso.split('-').map(Number);
	const date = new Date(y, m - 1, d + n);
	const pad = (x: number) => (x < 10 ? `0${x}` : `${x}`);
	return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

export const MOCK_API = `http://127.0.0.1:${process.env.MOCK_API_PORT ?? 8099}`;

/** Adds an account through the UI and returns to the caller on its detail page. */
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

/** The full bill form (Bills › + › More options lands here too). */
export async function addBill(
	page: Page,
	opts: {
		kind?: 'Bill' | 'Subscription' | 'Income';
		name: string;
		amount: string;
		firstDue: string;
		frequency?: string;
		bankNames?: string[];
	}
) {
	await page.goto('/bills/new');
	if (opts.kind) await page.getByRole('radiogroup', { name: 'Kind' }).getByText(opts.kind).click();
	await page.getByLabel('Name', { exact: true }).fill(opts.name);
	await page.getByLabel('Amount').fill(opts.amount);
	await page.getByLabel('Repeats').selectOption(opts.frequency ?? 'monthly');
	await page
		.getByLabel(opts.frequency === 'once' ? 'Date' : 'First due date', { exact: true })
		.fill(opts.firstDue);
	for (const n of opts.bankNames ?? []) {
		await page.getByLabel('Bank shows it as').fill(n);
		await page.getByLabel('Bank shows it as').press('Enter');
	}
	await page.getByRole('button', { name: 'Save', exact: true }).click();
	await expect(page).toHaveURL(/\/bills\/[0-9a-f-]+/);
	return page.url().split('?')[0];
}

/** Opens the + sheet on the current page, types a sentence and adds it. */
export async function quickAdd(page: Page, text: string) {
	await hydrated(page);
	await page.getByTestId('quick-add').click();
	// the sheet opens on Transaction; bills and one-offs are the second tab
	await page.getByTestId('quick-tab-bill').click();
	const sheet = page.getByTestId('quick-add-form');
	await sheet.getByTestId('quick-add-input').fill(text);
	await expect(sheet.getByTestId('quick-add-preview')).toBeVisible();
	const saved = page.waitForResponse(
		(r) => r.url().includes('/quickAdd') && r.request().method() === 'POST'
	);
	await sheet.getByTestId('quick-add-submit').click();
	await saved;
	await expect(page.getByTestId('quick-add-form')).toHaveCount(0);
}

/**
 * The demo banks only exist in the mock API's fake Bridge. Against a real API the claim form shows
 * the "not configured" state (no DATA_KEY) or rejects the token, so bank flows skip themselves.
 */
export async function skipUnlessMockBridge(page: Page) {
	await page.goto('/settings/banks');
	await expect(page.getByRole('heading', { name: 'How bank sync works' })).toBeVisible();
	await page.getByLabel('Setup token').fill('probe');
	await page.getByRole('button', { name: 'Link bank' }).click();
	await expect(page.getByRole('alert').or(page.getByTestId('banks-not-configured'))).toBeVisible();
	const notConfigured = await page.getByTestId('banks-not-configured').count();
	test.skip(notConfigured > 0, 'bank sync not configured on this API (demo bank is mock-only)');
}

/** Links a demo bank (`demo-token` or `demo-v5`) and waits for its two external accounts. */
export async function linkDemoBank(page: Page, token: 'demo-token' | 'demo-v5' = 'demo-token') {
	await page.goto('/settings/banks');
	await page.getByLabel('Name').fill('Demo Bank');
	await page.getByLabel('Setup token').fill(token);
	await page.getByRole('button', { name: 'Link bank' }).click();
	const linked = page.getByText(/Linked Demo Bank · 2 accounts found/);
	await expect(linked.or(page.getByRole('alert'))).toBeVisible();
	test.skip((await linked.count()) === 0, `the ${token} bank only exists in the mock API`);
	await expect(page.getByTestId('external-account')).toHaveCount(2);
}

export async function mapChecking(page: Page, accountName = 'Everyday') {
	await page
		.getByLabel('Mapped account for Demo Bank · Everyday Checking')
		.selectOption({ label: accountName });
	await expect(page.getByText(`Everyday Checking now updates ${accountName}`)).toBeVisible();
}

/** Runs the mock scheduler's bank sync for this user's household (optionally after a Bridge edit). */
export async function bankSync(page: Page, email: string, phase?: string) {
	const q = new URLSearchParams({ email });
	if (phase) q.set('phase', phase);
	const res = await page.request.post(`${MOCK_API}/__mock/bank-sync?${q}`);
	expect(res.ok()).toBeTruthy();
}

/** A To-review row on /transactions. */
export const reviewRow = (page: Page, text: string) =>
	page.getByTestId('review-row').filter({ hasText: text });
/** A handled row (Pending or posted) on /transactions or an account's Transactions tab. */
export const txRow = (page: Page, text: string) =>
	page.getByTestId('tx-row').filter({ hasText: text });
export const sheet = (page: Page) => page.getByTestId('transaction-sheet');

/** Swipes an element horizontally with pointer events (right = positive). */
export async function swipe(page: Page, locator: ReturnType<Page['locator']>, dx: number) {
	// centre it so the sticky header and toasts never sit under the pointer
	await locator.evaluate((el) => el.scrollIntoView({ block: 'center', behavior: 'instant' }));
	const box = await locator.boundingBox();
	if (!box) throw new Error('nothing to swipe');
	const y = box.y + 24;
	const x = box.x + box.width / 2;
	await page.mouse.move(x, y);
	await page.mouse.down();
	for (let i = 1; i <= 8; i++) await page.mouse.move(x + (dx * i) / 8, y);
	await page.mouse.up();
}

/** Waits until the app has hydrated (the (app) layout sets `html[data-ready]` on mount). */
export async function hydrated(page: Page) {
	await expect(page.locator('html[data-ready="1"]')).toHaveCount(1);
}

// ---- v7 --------------------------------------------------------------------------------------

export const WEEKDAY_NAMES = [
	'Sunday',
	'Monday',
	'Tuesday',
	'Wednesday',
	'Thursday',
	'Friday',
	'Saturday'
];
export const weekdayOf = (iso: string) => {
	const [y, m, d] = iso.split('-').map(Number);
	return new Date(y, m - 1, d).getDay();
};
/** The payday that starts the pay week containing `iso`. */
export const payWeekStart = (iso: string, payday: number) =>
	shiftDays(iso, -((weekdayOf(iso) - payday + 7) % 7));
export const usd = (cents: number) =>
	new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(cents / 100);

/** Links the mock's household-shaped bank (`demo-household`: US Bank, Ally HYS, two Ally checkings). */
export async function linkHouseholdBank(page: Page) {
	await page.goto('/settings/banks');
	await page.getByLabel('Name').fill('Our banks');
	await page.getByLabel('Setup token').fill('demo-household');
	await page.getByRole('button', { name: 'Link bank' }).click();
	const linked = page.getByText(/Linked Our banks · 4 accounts found/);
	await expect(linked.or(page.getByRole('alert'))).toBeVisible();
	test.skip((await linked.count()) === 0, 'the demo-household bank only exists in the mock API');
}

/**
 * The fast path through setup (steps 2–4 with their defaults): roles as guessed, payday (default
 * Friday, or `payday`), the found paycheck, $20 / $150 fun money. Lands on /week.
 */
export async function setupHousehold(page: Page, opts: { payday?: number } = {}) {
	await linkHouseholdBank(page);
	await page.goto('/setup?step=roles');
	await hydrated(page);
	await page.getByTestId('setup-save').click();
	await expect(page).toHaveURL(/step=payday/);
	if (opts.payday !== undefined)
		await page
			.getByRole('radiogroup', { name: 'Payday' })
			.getByLabel(WEEKDAY_NAMES[opts.payday])
			.check({ force: true });
	await expect(page.getByLabel('Paycheck amount')).toHaveValue('2450.00');
	await page.getByTestId('setup-save').click();
	await expect(page).toHaveURL(/step=fun/);
	await page.getByTestId('setup-save').click();
	await expect(page).toHaveURL(/step=notes/);
	await page.goto('/week');
	await expect(page.getByTestId('plan-receipt')).toBeVisible();
}

/** Opens "+" (it starts on Transaction) and adds one: `-6.50` money out, `2450` money in. */
export async function addTransaction(
	page: Page,
	opts: { amount: string; description: string; force?: boolean }
) {
	await hydrated(page);
	await page.getByTestId('quick-add').click();
	const form = page.getByTestId('add-tx-form');
	await expect(form).toBeVisible();
	const out = opts.amount.startsWith('-');
	const radio = form.getByTestId(out ? 'add-tx-out' : 'add-tx-in');
	await radio.locator('..').click();
	await expect(radio).toBeChecked();
	await form.getByTestId('add-tx-amount').fill(opts.amount.replace(/^-/, ''));
	await form.getByTestId('add-tx-description').fill(opts.description);
	const saved = page.waitForResponse(
		(r) => r.url().includes('/addTransaction') && r.request().method() === 'POST'
	);
	await form.getByTestId('add-tx-submit').click();
	await saved;
}
