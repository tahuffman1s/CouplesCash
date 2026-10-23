/**
 * The household's display currency (ISO 4217). Set once per render by the app layout from the
 * household settings; `formatMoney` falls back to it whenever a call site passes no currency.
 *
 * Reactive so money renders update when the household changes its currency. Svelte renders a
 * request's component tree synchronously on the server, so setting it at the top of the layout
 * applies to that whole render before the next request starts.
 */
let current = $state('USD');

export const currency = {
	get code(): string {
		return current;
	},
	set(code: string | null | undefined) {
		const next = (code ?? 'USD').toUpperCase();
		if (next !== current) current = next;
	}
};
