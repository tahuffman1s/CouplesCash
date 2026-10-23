import { currency } from '#lib/state/currency.svelte.ts';

export type MoneyOptions = {
	/** Always show a leading + for positive values. */
	signed?: boolean;
	/** Drop cents ($1,234). */
	whole?: boolean;
	/** $1.2K style, for tight spaces like chart labels. */
	compact?: boolean;
	/** ISO 4217 code; defaults to the household currency (USD when unknown). */
	currency?: string;
};

type Style = 'full' | 'whole' | 'compact';
const formatters = new Map<string, Intl.NumberFormat>();

function formatter(code: string, style: Style): Intl.NumberFormat {
	const key = `${code}:${style}`;
	let fmt = formatters.get(key);
	if (!fmt) {
		const base: Intl.NumberFormatOptions = { style: 'currency', currency: code };
		try {
			fmt = new Intl.NumberFormat(
				'en-US',
				style === 'compact'
					? { ...base, notation: 'compact', maximumFractionDigits: 1 }
					: style === 'whole'
						? { ...base, maximumFractionDigits: 0 }
						: base // the currency's own minor digits (2 for USD/EUR, 0 for JPY)
			);
		} catch {
			// unknown code (free-text input) – fall back to USD so the UI never throws
			return formatter('USD', style);
		}
		formatters.set(key, fmt);
	}
	return fmt;
}

/**
 * Formats integer cents (always hundredths – the API is currency-agnostic) in the given or
 * household currency. Negative values render with a leading minus; `opts.currency` wins over the
 * household default.
 */
export function formatMoney(cents: number, opts: MoneyOptions = {}): string {
	const code = (opts.currency ?? currency.code ?? 'USD').toUpperCase();
	const safe = Number.isFinite(cents) ? Math.round(cents) : 0;
	const units = safe / 100;
	const fmt = formatter(code, opts.compact ? 'compact' : opts.whole ? 'whole' : 'full');
	const text = fmt.format(Math.abs(units));
	if (safe < 0) return `-${text}`;
	if (opts.signed && safe > 0) return `+${text}`;
	return text;
}

/** Just the symbol for a currency ("$", "€", "£"), for input adornments. */
export function currencySymbol(code: string = currency.code): string {
	try {
		const parts = new Intl.NumberFormat('en-US', {
			style: 'currency',
			currency: code,
			currencyDisplay: 'narrowSymbol'
		}).formatToParts(0);
		return parts.find((p) => p.type === 'currency')?.value ?? code;
	} catch {
		return code;
	}
}

/** Parses a user-typed amount ("1,234.5", "$12", "-3") into integer cents. Returns null if invalid. */
export function parseMoney(input: string | null | undefined): number | null {
	if (input == null) return null;
	const cleaned = input.replace(/[$€£¥₹,\s]/g, '');
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
