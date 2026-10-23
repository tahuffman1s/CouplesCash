/**
 * Normalises whatever was typed or scanned into something `/couple/join` accepts:
 * the 8-char typed code (dashes/case ignored) or the full `cc1.` payload. Deep links from a
 * natively scanned QR (`https://host/couple/join?c=cc1...`) are unwrapped too.
 */
export function normaliseCoupleCode(input: string): string | null {
	let value = input.trim();
	if (!value) return null;
	// deep link
	if (/^https?:\/\//i.test(value)) {
		try {
			const c = new URL(value).searchParams.get('c');
			if (c) value = c;
		} catch {
			return null;
		}
	}
	if (value.startsWith('cc1.')) {
		return /^cc1\.[A-Za-z0-9_-]{20,40}$/.test(value) ? value : null;
	}
	const compact = value.replace(/[\s-]/g, '').toUpperCase();
	if (!/^[0-9A-Z]{8}$/.test(compact)) return null;
	return `${compact.slice(0, 4)}-${compact.slice(4)}`;
}
