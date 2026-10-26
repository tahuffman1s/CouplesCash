import { describe, expect, it } from 'vitest';
import {
	cleanNoteLine,
	guessNoteAccount,
	parseNotes,
	readNoteLine,
	skipReason,
	splitNoteLines
} from './notes.ts';

const TODAY = '2026-10-26'; // a Monday

describe('cleanNoteLine', () => {
	it('strips bullets, checkboxes and numbering', () => {
		expect(cleanNoteLine('- Rent 1850 1st')).toBe('Rent 1850 1st');
		expect(cleanNoteLine('• Netflix 15.49 7th')).toBe('Netflix 15.49 7th');
		expect(cleanNoteLine('* Water ~60 20th')).toBe('Water ~60 20th');
		expect(cleanNoteLine('1. Rent 1850 1st')).toBe('Rent 1850 1st');
		expect(cleanNoteLine('12) Rent 1850 1st')).toBe('Rent 1850 1st');
		expect(cleanNoteLine('[ ] Rent 1850 1st')).toBe('Rent 1850 1st');
		expect(cleanNoteLine('[x] Rent 1850 1st')).toBe('Rent 1850 1st');
		expect(cleanNoteLine('- [ ] Rent 1850 1st')).toBe('Rent 1850 1st');
		expect(cleanNoteLine('☐ Rent 1850 1st')).toBe('Rent 1850 1st');
		expect(cleanNoteLine('✓ Rent 1850 1st')).toBe('Rent 1850 1st');
		expect(cleanNoteLine('–\tRent   1850  1st ')).toBe('Rent 1850 1st');
	});
	it('leaves ordinary text alone', () => {
		expect(cleanNoteLine('Car ins 216 every 6 months')).toBe('Car ins 216 every 6 months');
		expect(cleanNoteLine('a. Rent 1850')).toBe('Rent 1850');
		expect(cleanNoteLine('Ally 20')).toBe('Ally 20');
		expect(cleanNoteLine('')).toBe('');
	});
});

describe('splitNoteLines', () => {
	it('splits any newline style and drops blank lines', () => {
		expect(splitNoteLines('Rent 1850 1st\r\n\r\nNetflix 15.49 7th\rWater ~60 20th\n- \n')).toEqual([
			'Rent 1850 1st',
			'Netflix 15.49 7th',
			'Water ~60 20th'
		]);
		expect(splitNoteLines('   \n\n')).toEqual([]);
	});
});

describe('skipReason', () => {
	it('calculations, totals and headings', () => {
		expect(skipReason('2345.67 - 1992.50 = 353.17')).toBe('Looks like a calculation');
		expect(skipReason('2345.67 - 1992.50')).toBe('Looks like a calculation');
		expect(skipReason('1850+60')).toBe('Looks like a calculation');
		expect(skipReason('Total 2345.67')).toBe('Looks like a total');
		expect(skipReason('Left over: 253.17')).toBe('Looks like a total');
		expect(skipReason('Balance 2408.07')).toBe('Looks like a total');
		expect(skipReason('Bills:')).toBe('Looks like a heading');
		expect(skipReason('')).toBe('Empty line');
	});
	it('a bill with a dash or a range is not a calculation', () => {
		expect(skipReason('Rent - 1850 - 1st')).toBeNull();
		expect(skipReason('Car ins 216 every 6 months')).toBeNull();
		expect(skipReason('Phone 85 on the 12th')).toBeNull();
	});
});

describe('readNoteLine', () => {
	it('reads her lines with the quick-add grammar', () => {
		const rent = readNoteLine('Rent 1850 1st', TODAY);
		expect(rent.skip).toBeNull();
		expect(rent.draft).toMatchObject({
			name: 'Rent',
			amountCents: 185000,
			cadence: 'monthly',
			anchorDate: '2026-11-01'
		});
		const netflix = readNoteLine('Netflix 15.49 7th', TODAY).draft!;
		expect(netflix).toMatchObject({ kind: 'subscription', amountCents: 1549, cadence: 'monthly' });
		expect(netflix.anchorDate).toBe('2026-11-07');
		const car = readNoteLine('Car ins 216 every 6 months', TODAY).draft!;
		expect(car).toMatchObject({ name: 'Car ins', amountCents: 21600, cadence: 'semiannual' });
		const water = readNoteLine('Water ~60 20th', TODAY).draft!;
		expect(water).toMatchObject({ name: 'Water', amountCents: 6000, amountVaries: true });
		expect(water.anchorDate).toBe('2026-11-20');
	});
	it('handles note punctuation', () => {
		expect(readNoteLine('- Rent: $1,850 (1st)', TODAY).draft).toMatchObject({
			name: 'Rent',
			amountCents: 185000,
			anchorDate: '2026-11-01'
		});
		expect(readNoteLine('Rent - 1850 - 1st', TODAY).draft).toMatchObject({
			name: 'Rent',
			amountCents: 185000
		});
		expect(readNoteLine('Student loan 312 15th', TODAY).draft).toMatchObject({
			name: 'Student loan',
			amountCents: 31200,
			anchorDate: '2026-11-15'
		});
	});
	it('says why a line was skipped', () => {
		expect(readNoteLine('Call the bank about the card', TODAY)).toMatchObject({
			draft: null,
			skip: 'No amount'
		});
		expect(readNoteLine('$50', TODAY)).toMatchObject({ draft: null, skip: 'No name' });
		expect(readNoteLine('= 253.17', TODAY).skip).toBe('Looks like a calculation');
		expect(readNoteLine('  - Bills:', TODAY).skip).toBe('Looks like a heading');
	});
	it('keeps the raw line and its index', () => {
		const l = readNoteLine('• Rent 1850 1st', TODAY, 4);
		expect(l).toMatchObject({ index: 4, raw: '• Rent 1850 1st', text: 'Rent 1850 1st' });
	});
});

describe('parseNotes', () => {
	it('a real paste: six lines, one unreadable', () => {
		const lines = parseNotes(
			[
				'Bills',
				'- Rent 1850 1st',
				'- Netflix 15.49 7th',
				'',
				'- Car ins 216 every 6 months',
				'- Water ~60 20th',
				'- ask Travis about the gym',
				'- Phone 85 12th'
			].join('\n'),
			TODAY
		);
		expect(lines.map((l) => l.skip)).toEqual([
			'No amount',
			null,
			null,
			null,
			null,
			'No amount',
			null
		]);
		expect(lines.filter((l) => l.draft).map((l) => l.draft!.name)).toEqual([
			'Rent',
			'Netflix',
			'Car ins',
			'Water',
			'Phone'
		]);
		expect(lines.map((l) => l.index)).toEqual([0, 1, 2, 3, 4, 5, 6]);
	});
	it('an empty paste is no lines', () => {
		expect(parseNotes('', TODAY)).toEqual([]);
	});
});

describe('guessNoteAccount', () => {
	const ids = { billsId: 'bills', savingsId: 'savings' };
	it('student loans come out of savings, the rest out of bills', () => {
		expect(guessNoteAccount('Student loan', ids)).toBe('savings');
		expect(guessNoteAccount('Student loans', ids)).toBe('savings');
		expect(guessNoteAccount('Nelnet', ids)).toBe('savings');
		expect(guessNoteAccount('Rent', ids)).toBe('bills');
		expect(guessNoteAccount('Student loan', { billsId: 'bills', savingsId: null })).toBe('bills');
		expect(guessNoteAccount('Rent', { billsId: null, savingsId: null })).toBeNull();
	});
});
