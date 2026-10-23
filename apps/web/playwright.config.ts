import { defineConfig, devices } from '@playwright/test';
import { existsSync, readdirSync } from 'node:fs';
import path from 'node:path';

const MOCK_PORT = 8099;
const WEB_PORT = 4173;

/**
 * CI containers ship a preinstalled Chromium under PLAYWRIGHT_BROWSERS_PATH whose revision may not
 * match the one this Playwright version expects. Rather than downloading another browser, point
 * at whatever Chromium is there. `CHROMIUM_PATH` overrides everything.
 */
function chromiumExecutable(): string | undefined {
	if (process.env.CHROMIUM_PATH) return process.env.CHROMIUM_PATH;
	const root = process.env.PLAYWRIGHT_BROWSERS_PATH;
	if (!root || !existsSync(root)) return undefined;
	const dirs = readdirSync(root).filter((d) => d.startsWith('chromium'));
	const candidates = dirs.flatMap((d) => [
		path.join(root, d, 'chrome-linux', 'headless_shell'),
		path.join(root, d, 'chrome-linux', 'chrome'),
		path.join(root, d, 'chrome-headless-shell-linux64', 'chrome-headless-shell')
	]);
	// prefer the headless shell (what Playwright uses for headless runs), then full Chromium
	const shell = candidates.find((c) => c.includes('headless') && existsSync(c));
	return shell ?? candidates.find((c) => existsSync(c));
}

const executablePath = chromiumExecutable();

export default defineConfig({
	testDir: 'e2e',
	testMatch: '**/*.e2e.ts',
	fullyParallel: true,
	retries: process.env.CI ? 1 : 0,
	reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
	timeout: 30_000,
	use: {
		baseURL: `http://localhost:${WEB_PORT}`,
		trace: 'retain-on-failure',
		// Only Chromium is installed in CI containers; the device descriptors give us the mobile
		// viewports, touch and user agents we care about.
		browserName: 'chromium',
		launchOptions: executablePath ? { executablePath } : {}
	},
	projects: [
		{ name: 'iPhone 14', use: { ...devices['iPhone 14'], browserName: 'chromium' } },
		{ name: 'Pixel 7', use: { ...devices['Pixel 7'], browserName: 'chromium' } }
	],
	webServer: [
		{
			command: 'node e2e/mock-api.ts',
			port: MOCK_PORT,
			reuseExistingServer: !process.env.CI,
			env: { MOCK_API_PORT: String(MOCK_PORT) }
		},
		{
			command: 'pnpm build && pnpm preview --port 4173 --strictPort',
			port: WEB_PORT,
			reuseExistingServer: !process.env.CI,
			timeout: 180_000,
			env: { API_URL: `http://127.0.0.1:${MOCK_PORT}`, ORIGIN: `http://localhost:${WEB_PORT}` }
		}
	]
});
