/**
 * v7 "Paste your notes": her bill list from the notes app, one bill per line, read with the
 * quick-add grammar (`quickadd.ts`). Pure and unit-tested. Bullets, checkboxes and numbering are
 * stripped; headings, totals and calculations ("2345.67 − 1992.50 = 353.17") are kept visible as
 * skipped lines so nothing she pasted is silently lost.
 */
import { draftProblems, parseQuickAdd, type QuickAddDraft } from './quickadd.ts';

export type NoteLine = {
	/** 0-based position among the pasted non-empty lines */
	index: number;
	raw: string;
	/** the line without its bullet / checkbox */
	text: string;
	draft: QuickAddDraft | null;
	/** why it was skipped (null = a bill) */
	skip: string | null;
};

const BULLET_RE =
	/^\s*(?:[-*•·◦▪‣–—+>]+|\[[ xX✓✔]?\]|[☐☑☒✓✔✗✘]|\(?\d{1,2}[.)]|[a-zA-Z][.)](?=\s))\s*/u;

/** Strips one leading bullet, checkbox or "1." / "a)" marker and collapses spaces. */
export function cleanNoteLine(raw: string): string {
	let text = raw.replace(/[\u00a0\t]/g, ' ').trim();
	// a checkbox after a bullet ("- [ ] Rent") goes too
	for (let i = 0; i < 2; i++) {
		const next = text.replace(BULLET_RE, '');
		if (next === text) break;
		text = next;
	}
	return text.replace(/\s+/g, ' ').trim();
}

/** Non-empty lines of a paste (any newline style). */
export function splitNoteLines(text: string): string[] {
	return text
		.split(/\r\n|\r|\n/)
		.map((l) => l.trim())
		.filter((l) => cleanNoteLine(l).length > 0);
}

const AMOUNT_ANYWHERE = /\$?\d[\d,]*(?:\.\d{1,2})?/g;
const TOTAL_RE = /^(?:total|totals|subtotal|sum|left|leftover|left over|balance|remaining)\b/i;

/** Why a cleaned line is not a bill, before parsing; null when it might be one. */
export function skipReason(text: string): string | null {
	if (!text) return 'Empty line';
	if (/[=]/.test(text)) return 'Looks like a calculation';
	const amounts = text.match(AMOUNT_ANYWHERE) ?? [];
	// "2345.67 - 1992.50" / "1850 + 60": two money-looking numbers joined by an operator
	if (amounts.length >= 2 && /\d\s*[-−+*/x]\s*\$?\d/.test(text) && !/[a-z]{3,}/i.test(text))
		return 'Looks like a calculation';
	if (TOTAL_RE.test(text)) return 'Looks like a total';
	if (!/\d/.test(text) && /:$/.test(text)) return 'Looks like a heading';
	return null;
}

/** Reads one pasted line: a draft bill, or the reason it was skipped. */
export function readNoteLine(raw: string, today: string, index = 0): NoteLine {
	const text = cleanNoteLine(raw);
	const early = skipReason(text);
	if (early) return { index, raw, text, draft: null, skip: early };
	const draft = parseQuickAdd(text, today);
	const problems = draftProblems(draft);
	if (problems.length) {
		const noAmount = draft.amountCents === null || draft.amountCents <= 0;
		return {
			index,
			raw,
			text,
			draft: null,
			skip: noAmount ? 'No amount' : 'No name'
		};
	}
	return { index, raw, text, draft, skip: null };
}

/** Every line of a paste, in order. */
export function parseNotes(text: string, today: string): NoteLine[] {
	return splitNoteLines(text).map((l, i) => readNoteLine(l, today, i));
}

/**
 * Which account a pasted bill most likely comes out of: student loans come out of savings for
 * them (PLAN-V7); everything else from the bills account.
 */
export function guessNoteAccount(
	name: string,
	accounts: { billsId: string | null; savingsId: string | null }
): string | null {
	if (
		accounts.savingsId &&
		/\b(student loans?|navient|nelnet|mohela|sallie mae|fedloan)\b/i.test(name)
	)
		return accounts.savingsId;
	return accounts.billsId;
}
