const usd = new Intl.NumberFormat('en-US', {
	style: 'currency',
	currency: 'USD',
	minimumFractionDigits: 2,
	maximumFractionDigits: 2
});
const usdWhole = new Intl.NumberFormat('en-US', {
	style: 'currency',
	currency: 'USD',
	maximumFractionDigits: 0
});
const usdCompact = new Intl.NumberFormat('en-US', {
	style: 'currency',
	currency: 'USD',
	notation: 'compact',
	maximumFractionDigits: 1
});

export type MoneyOptions = {
	/** Always show a leading + for positive values. */
	signed?: boolean;
	/** Drop cents ($1,234). */
	whole?: boolean;
	/** $1.2K style, for tight spaces like chart labels. */
	compact?: boolean;
};

/** Formats integer cents as USD. Negative values render with a leading minus. */
export function formatMoney(cents: number, opts: MoneyOptions = {}): string {
	const safe = Number.isFinite(cents) ? Math.round(cents) : 0;
	const dollars = safe / 100;
	const fmt = opts.compact ? usdCompact : opts.whole ? usdWhole : usd;
	const text = fmt.format(Math.abs(dollars));
	if (safe < 0) return `-${text}`;
	if (opts.signed && safe > 0) return `+${text}`;
	return text;
}

/** Parses a user-typed amount ("1,234.5", "$12", "-3") into integer cents. Returns null if invalid. */
export function parseMoney(input: string | null | undefined): number | null {
	if (input == null) return null;
	const cleaned = input.replace(/[$,\s]/g, '');
	if (
		cleaned === '' ||
		!/^-?\d*(\.\d{0,2})?$/.test(cleaned) ||
		cleaned === '-' ||
		cleaned === '.'
	) {
		return null;
	}
	const negative = cleaned.startsWith('-');
	const [whole = '0', frac = ''] = cleaned.replace('-', '').split('.');
	const cents = Number(whole) * 100 + Number(frac.padEnd(2, '0').slice(0, 2));
	return negative ? -cents : cents;
}

/** Cents → "1234.56" for pre-filling number inputs. */
export function centsToInput(cents: number): string {
	return (Math.round(cents) / 100).toFixed(2);
}
