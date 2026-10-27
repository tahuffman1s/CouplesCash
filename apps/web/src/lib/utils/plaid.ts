import type { BankLink, PlaidLinkToken, SyncResult } from '#lib/types.ts';
import { toasts } from '#lib/state/toasts.svelte.ts';
import { apiFetch } from './client.ts';

/**
 * Plaid Link in the browser (https://plaid.com/docs/link/web/). The page asks the API for a short-lived link token, Plaid's
 * own window signs in to the bank, and its public token goes straight back to the API, which exchanges and stores the access
 * token encrypted; the browser never sees a credential. OAuth banks (US Bank, Chase, …) on a phone leave the page and come
 * back to `/settings/banks/plaid-oauth`, which finishes the same flow from what was saved in sessionStorage.
 */
export const PLAID_SCRIPT = 'https://cdn.plaid.com/link/v2/stable/link-initialize.js';
const STORAGE_KEY = 'cc-plaid-link';

type PlaidMetadata = { institution?: { name?: string | null } | null };
type PlaidHandler = { open: () => void; exit?: (opts?: unknown) => void; destroy?: () => void };
type PlaidCreateOptions = {
	token: string;
	receivedRedirectUri?: string;
	onSuccess: (publicToken: string, metadata: PlaidMetadata) => void;
	onExit?: (error: { display_message?: string | null; error_message?: string } | null) => void;
};
declare global {
	interface Window {
		Plaid?: { create: (options: PlaidCreateOptions) => PlaidHandler };
	}
}

/** What a finished Link flow produced: a new link, or a reconnected one (and its sync). */
export type PlaidOutcome =
	| { kind: 'linked'; link: BankLink }
	| { kind: 'reconnected'; result: SyncResult }
	| { kind: 'exit'; message: string | null };

type Saved = { token: string; linkId: string | null };

let loading: Promise<NonNullable<Window['Plaid']>> | null = null;

/** Loads Plaid's Link script once (CSP allows cdn.plaid.com). */
export function loadPlaid(): Promise<NonNullable<Window['Plaid']>> {
	if (window.Plaid) return Promise.resolve(window.Plaid);
	loading ??= new Promise((resolve, reject) => {
		const script = document.createElement('script');
		script.src = PLAID_SCRIPT;
		script.async = true;
		script.onload = () =>
			window.Plaid ? resolve(window.Plaid) : reject(new Error('Plaid did not load'));
		script.onerror = () => {
			loading = null;
			reject(new Error('Could not load Plaid. Check your connection.'));
		};
		document.head.appendChild(script);
	});
	return loading;
}

function save(saved: Saved | null) {
	try {
		if (saved) sessionStorage.setItem(STORAGE_KEY, JSON.stringify(saved));
		else sessionStorage.removeItem(STORAGE_KEY);
	} catch {
		// private mode: OAuth banks then need the pop-up flow
	}
}

export function savedLink(): Saved | null {
	try {
		const raw = sessionStorage.getItem(STORAGE_KEY);
		const parsed = raw ? (JSON.parse(raw) as Partial<Saved>) : null;
		return parsed?.token ? { token: parsed.token, linkId: parsed.linkId ?? null } : null;
	} catch {
		return null;
	}
}

/** The API side of a successful Link: exchange (new connection) or "reconnected" (update mode). */
async function finish(
	saved: Saved,
	publicToken: string,
	metadata: PlaidMetadata
): Promise<PlaidOutcome> {
	save(null);
	if (saved.linkId) {
		const result = await apiFetch<SyncResult>(`/banks/${saved.linkId}/plaid/reconnected`, {
			method: 'POST'
		});
		return { kind: 'reconnected', result };
	}
	const link = await apiFetch<BankLink>('/banks/plaid/exchange', {
		method: 'POST',
		body: JSON.stringify({ publicToken, institutionName: metadata.institution?.name ?? null })
	});
	return { kind: 'linked', link };
}

function run(saved: Saved, receivedRedirectUri?: string): Promise<PlaidOutcome> {
	return loadPlaid().then(
		(plaid) =>
			new Promise<PlaidOutcome>((resolve, reject) => {
				const handler = plaid.create({
					token: saved.token,
					receivedRedirectUri,
					onSuccess: (publicToken, metadata) => {
						finish(saved, publicToken, metadata).then(resolve, reject);
					},
					onExit: (error) => {
						save(null);
						resolve({
							kind: 'exit',
							message: error ? (error.display_message ?? error.error_message ?? null) : null
						});
					}
				});
				handler.open();
			})
	);
}

/** Opens Plaid Link for a new bank, or — with `linkId` — to sign in to that bank again. */
export async function openPlaid(linkId: string | null = null): Promise<PlaidOutcome> {
	const { linkToken } = await apiFetch<PlaidLinkToken>('/banks/plaid/link-token', {
		method: 'POST',
		body: JSON.stringify(linkId ? { linkId } : {})
	});
	const saved = { token: linkToken, linkId };
	save(saved);
	return run(saved);
}

/** Back from an OAuth bank: re-open Link with the URL it returned to (null when nothing was started here). */
export function resumePlaid(href: string): Promise<PlaidOutcome> | null {
	const saved = savedLink();
	return saved ? run(saved, href) : null;
}

/** Toasts for a finished Link flow (Settings → Banks and the OAuth return page). */
export function announcePlaid(outcome: PlaidOutcome) {
	if (outcome.kind === 'linked') {
		const n = outcome.link.accounts.length;
		toasts.show(`Linked ${outcome.link.name} · ${n} account${n === 1 ? '' : 's'} found`, 'success');
		if (outcome.link.bridgeErrors.length) toasts.show(outcome.link.bridgeErrors[0], 'info', 6000);
	} else if (outcome.kind === 'reconnected') {
		toasts.show('Reconnected · syncing', 'success');
	} else if (outcome.message) {
		toasts.show(outcome.message, 'error', 6000);
	}
}
