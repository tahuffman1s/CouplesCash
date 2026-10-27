import { fail, type RequestEvent } from '@sveltejs/kit';
import { endpoints, isApiError } from './api';
import { str } from './forms';
import { isValidISODate } from '#lib/utils/dates.ts';
import { parseMoney } from '#lib/utils/money.ts';

export type AddTransactionValues = {
	direction: 'out' | 'in';
	amount: string;
	description: string;
	accountId: string;
	postedOn: string;
};

export type AddTransactionForm = {
	added?: { description: string; amountCents: number };
	addError?: string;
	/** the bank already shows something like it: the sheet offers "Add anyway" */
	addDuplicate?: boolean;
	addValues?: AddTransactionValues;
};

/**
 * The "+" sheet's Transaction tab (`?/addTransaction` on Week, Bills and Transactions): a charge the
 * bank hasn't shown yet. `force=1` (the "Add anyway" button) stores it even when the bank already
 * shows something that looks the same.
 */
export async function addTransactionAction(event: RequestEvent) {
	const data = await event.request.formData();
	const values: AddTransactionValues = {
		direction: str(data, 'direction') === 'in' ? 'in' : 'out',
		amount: str(data, 'amount'),
		description: str(data, 'description').slice(0, 120),
		accountId: str(data, 'accountId'),
		postedOn: str(data, 'postedOn')
	};
	const cents = parseMoney(values.amount);
	if (cents === null || cents <= 0)
		return fail(400, { addError: 'Enter an amount.', addValues: values });
	if (!values.description.trim())
		return fail(400, { addError: 'Say what it was.', addValues: values });
	if (!values.accountId) return fail(400, { addError: 'Pick an account.', addValues: values });
	if (!isValidISODate(values.postedOn))
		return fail(400, { addError: 'Pick a date.', addValues: values });

	const amountCents = values.direction === 'in' ? cents : -cents;
	try {
		await endpoints(event).transactions.add({
			accountId: values.accountId,
			amountCents,
			description: values.description.trim(),
			postedOn: values.postedOn,
			force: data.get('force') === '1'
		});
	} catch (e) {
		if (isApiError(e, 409))
			return fail(409, {
				addError: e.problem.detail ?? 'The bank already shows this.',
				addDuplicate: true,
				addValues: values
			});
		return fail(isApiError(e) ? e.status : 500, {
			addError: isApiError(e) ? (e.field('postedOn') ?? e.message) : 'Could not save. Try again.',
			addValues: values
		});
	}
	return { added: { description: values.description.trim(), amountCents } };
}
