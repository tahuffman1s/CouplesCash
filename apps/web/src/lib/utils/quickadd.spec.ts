import { describe, expect, it } from 'vitest';
import {
	cadenceLabel,
	draftProblems,
	guessCategory,
	guessKind,
	parseAmountToken,
	parseQuickAdd,
	scheduleText,
	secondAnchor,
	shortDate,
	toRecurringBodies
} from './quickadd.ts';

// Monday 26 October 2026
const TODAY = '2026-10-26';
const p = (text: string) => parseQuickAdd(text, TODAY);

describe('quick add · the plan examples', () => {
	it('netflix 15.49 on the 7th → subscription, monthly, day 7', () => {
		const d = p('netflix 15.49 on the 7th');
		expect(d).toMatchObject({
			name: 'Netflix',
			kind: 'subscription',
			amountCents: 1549,
			cadence: 'monthly',
			anchorDate: '2026-11-07',
			category: 'Subscriptions',
			bankName: 'Netflix',
			amountVaries: false,
			autoPay: false
		});
	});
	it('rent 1850 1st autopay → bill, monthly, day 1, autopay', () => {
		expect(p('rent 1850 1st autopay')).toMatchObject({
			name: 'Rent',
			kind: 'bill',
			amountCents: 185000,
			cadence: 'monthly',
			anchorDate: '2026-11-01',
			autoPay: true,
			category: 'Housing'
		});
	});
	it('oil change 60 friday → one-off this Friday', () => {
		expect(p('oil change 60 friday')).toMatchObject({
			name: 'Oil change',
			kind: 'bill',
			amountCents: 6000,
			cadence: 'once',
			anchorDate: '2026-10-30',
			category: 'Transportation'
		});
	});
	it('paycheck 3200 every other friday → income, biweekly from Friday', () => {
		expect(p('paycheck 3200 every other friday')).toMatchObject({
			name: 'Paycheck',
			kind: 'income',
			amountCents: 320000,
			cadence: 'biweekly',
			anchorDate: '2026-10-30',
			category: 'Income',
			autoPay: false
		});
	});
	it('car insurance 216 every 6 months → twice a year from today', () => {
		expect(p('car insurance 216 every 6 months')).toMatchObject({
			name: 'Car insurance',
			amountCents: 21600,
			cadence: 'semiannual',
			anchorDate: TODAY,
			category: 'Insurance'
		});
	});
	it('gym 45 weekly → weekly from today', () => {
		expect(p('gym 45 weekly')).toMatchObject({
			name: 'Gym',
			amountCents: 4500,
			cadence: 'weekly',
			anchorDate: TODAY,
			category: 'Health'
		});
	});
	it('water bill ~60 on the 20th → amount varies, monthly, day 20', () => {
		expect(p('water bill ~60 on the 20th')).toMatchObject({
			name: 'Water bill',
			kind: 'bill',
			amountCents: 6000,
			amountVaries: true,
			cadence: 'monthly',
			anchorDate: '2026-11-20',
			category: 'Utilities'
		});
	});
});

describe('quick add · amounts', () => {
	it.each([
		['15.49', 1549, false],
		['$15.49', 1549, false],
		['1,850', 185000, false],
		['$1,850.00', 185000, false],
		['~60', 6000, true],
		['~$60', 6000, true],
		['3.2k', 320000, false],
		['.99', 99, false],
		['12', 1200, false]
	])('%s → %i cents (varies %s)', (token, cents, varies) => {
		expect(parseAmountToken(token)).toEqual({ cents, varies });
	});
	it('rejects things that are not amounts', () => {
		expect(parseAmountToken('7th')).toBeNull();
		expect(parseAmountToken('11/3')).toBeNull();
		expect(parseAmountToken('abc')).toBeNull();
		expect(parseAmountToken('1,85')).toBeNull();
	});
	it('"about" and "around" mark the amount as varying', () => {
		expect(p('electric about 120 monthly')).toMatchObject({
			amountCents: 12000,
			amountVaries: true
		});
		expect(p('phone around $80 on the 3rd')).toMatchObject({
			amountCents: 8000,
			amountVaries: true,
			name: 'Phone'
		});
	});
	it('"~ 60" with a space still varies', () => {
		expect(p('water ~ 60')).toMatchObject({ amountCents: 6000, amountVaries: true });
	});
	it('a missing amount is reported, not guessed', () => {
		const d = p('netflix monthly');
		expect(d.amountCents).toBeNull();
		expect(draftProblems(d)).toEqual(['Add an amount, like 15.49']);
	});
	it('the first number is the amount; the order of words does not matter', () => {
		expect(p('15.49 netflix')).toMatchObject({ name: 'Netflix', amountCents: 1549 });
		expect(p('$1,850 rent')).toMatchObject({ name: 'Rent', amountCents: 185000 });
	});
});

describe('quick add · frequencies', () => {
	it.each([
		['gym 45 every week', 'weekly'],
		['gym 45 each week', 'weekly'],
		['gym 45 a week', 'weekly'],
		['gym 45 per week', 'weekly'],
		['cleaner 90 biweekly', 'biweekly'],
		['cleaner 90 bi-weekly', 'biweekly'],
		['cleaner 90 every other week', 'biweekly'],
		['cleaner 90 every 2 weeks', 'biweekly'],
		['cleaner 90 every two weeks', 'biweekly'],
		['cleaner 90 fortnightly', 'biweekly'],
		['paycheck 2000 twice a month', 'semimonthly'],
		['phone 80 monthly', 'monthly'],
		['phone 80 every month', 'monthly'],
		['phone 80 a month', 'monthly'],
		['water 120 quarterly', 'quarterly'],
		['water 120 every 3 months', 'quarterly'],
		['water 120 every three months', 'quarterly'],
		['car insurance 216 twice a year', 'semiannual'],
		['domain 15 yearly', 'yearly'],
		['domain 15 annually', 'yearly'],
		['domain 15 every year', 'yearly'],
		['gift 50 once', 'once'],
		['gift 50 one-off', 'once'],
		['gift 50 one time', 'once'],
		['gift 50 one off', 'once']
	])('%s → %s', (text, cadence) => {
		expect(p(text).cadence).toBe(cadence);
		expect(p(text).explicit.cadence).toBe(true);
	});
	it('every <weekday> is weekly from that weekday', () => {
		expect(p('cleaner 40 every thursday')).toMatchObject({
			cadence: 'weekly',
			anchorDate: '2026-10-29',
			name: 'Cleaner'
		});
	});
	it('no date and no frequency → monthly from today', () => {
		expect(p('rent 1850')).toMatchObject({ cadence: 'monthly', anchorDate: TODAY });
		expect(p('rent 1850').explicit).toMatchObject({ cadence: false, date: false });
	});
	it('cadence labels read like a sentence', () => {
		expect(cadenceLabel('biweekly')).toBe('Every 2 weeks');
		expect(cadenceLabel('semiannual')).toBe('Every 6 months');
	});
});

describe('quick add · dates', () => {
	it('today and tomorrow are one-offs', () => {
		expect(p('groceries 54 today')).toMatchObject({
			cadence: 'once',
			anchorDate: TODAY,
			category: 'Groceries'
		});
		expect(p('pizza 30 tomorrow')).toMatchObject({ cadence: 'once', anchorDate: '2026-10-27' });
	});
	it('a weekday is the next one, today included; "next" never means today', () => {
		expect(p('lunch 12 monday').anchorDate).toBe(TODAY);
		expect(p('lunch 12 next monday').anchorDate).toBe('2026-11-02');
		expect(p('lunch 12 next friday').anchorDate).toBe('2026-10-30');
		expect(p('lunch 12 this friday').anchorDate).toBe('2026-10-30');
		expect(p('lunch 12 tues').anchorDate).toBe('2026-10-27');
	});
	it('day of month: the 7th / on the 7th / 7th / the 7 all mean monthly on day 7', () => {
		for (const t of ['the 7th', 'on the 7th', '7th', 'the 7']) {
			const d = p(`netflix 15.49 ${t}`);
			expect(d).toMatchObject({ cadence: 'monthly', anchorDate: '2026-11-07', name: 'Netflix' });
			expect(d.amountCents).toBe(1549);
		}
	});
	it('a day of month still to come this month stays in this month', () => {
		expect(p('phone 80 on the 28th').anchorDate).toBe('2026-10-28');
		expect(p('phone 80 on the 26th').anchorDate).toBe(TODAY);
	});
	it('day 31 clamps to short months', () => {
		expect(parseQuickAdd('rent 1000 31st', '2026-11-05').anchorDate).toBe('2026-11-30');
	});
	it('month names: Nov 3, November 3rd, 3 Nov, 3rd of November', () => {
		for (const t of ['Nov 3', 'November 3rd', '3 Nov', '3rd of November', 'nov 3rd']) {
			expect(p(`tax refund 800 ${t}`)).toMatchObject({
				anchorDate: '2026-11-03',
				cadence: 'once',
				kind: 'income',
				name: 'Tax refund'
			});
		}
	});
	it('numeric dates: 11/3, 11/3/2026, 11/3/27 and ISO', () => {
		expect(p('dentist 120 11/3').anchorDate).toBe('2026-11-03');
		expect(p('dentist 120 11/3/2026').anchorDate).toBe('2026-11-03');
		expect(p('dentist 120 11/3/27').anchorDate).toBe('2027-11-03');
		expect(p('dentist 120 2026-12-01').anchorDate).toBe('2026-12-01');
	});
	it('an explicit date more than a week ago means next year', () => {
		expect(p('gift 40 jan 5').anchorDate).toBe('2027-01-05');
		expect(p('gift 40 oct 22').anchorDate).toBe('2026-10-22'); // 4 days ago: kept
	});
	it('"25 dec 3" is $25 on Dec 3', () => {
		expect(p('wreath 25 dec 3')).toMatchObject({ amountCents: 2500, anchorDate: '2026-12-03' });
	});
	it('an explicit frequency wins over the one-off default', () => {
		expect(p('cleaner 90 weekly friday')).toMatchObject({
			cadence: 'weekly',
			anchorDate: '2026-10-30'
		});
		expect(p('insurance 600 yearly on nov 3')).toMatchObject({
			cadence: 'yearly',
			anchorDate: '2026-11-03'
		});
	});
});

describe('quick add · kinds, names and categories', () => {
	it.each([
		['salary 4000 monthly', 'income'],
		['wages 900 weekly', 'income'],
		['refund 30 today', 'income'],
		['bonus 500 dec 15', 'income'],
		['alex pay 2100 every other friday', 'income'],
		['spotify 11.99 monthly', 'subscription'],
		['disney+ 13.99 on the 2nd', 'subscription'],
		['pay rent 1850 1st', 'bill'],
		['electric 120 monthly', 'bill']
	])('%s → %s', (text, kind) => {
		expect(p(text).kind).toBe(kind);
	});
	it('kind markers are dropped from the name: "spotify sub 11.99"', () => {
		expect(p('spotify sub 11.99')).toMatchObject({ name: 'Spotify', kind: 'subscription' });
		expect(p('rental income 900 on the 1st')).toMatchObject({ name: 'Rental', kind: 'income' });
	});
	it('a lone marker is the name', () => {
		expect(p('income 500 monthly')).toMatchObject({ name: 'Income', kind: 'income' });
	});
	it('"water bill" keeps "bill" in its name', () => {
		expect(p('water bill 60').name).toBe('Water bill');
	});
	it('filler at the edges is trimmed, the middle is kept', () => {
		expect(p('transfer to savings 200 monthly').name).toBe('Transfer to savings');
		expect(p('the rent 1850 due on the 1st').name).toBe('Rent');
	});
	it('names keep the case people typed when it is not all lower case', () => {
		expect(p('HBO Max 15.99 monthly').name).toBe('HBO Max');
		expect(p('oil change 60 friday').name).toBe('Oil change');
	});
	it('autopay spellings', () => {
		expect(p('rent 1850 auto pay').autoPay).toBe(true);
		expect(p('rent 1850 auto-pay').autoPay).toBe(true);
		expect(p('paycheck 3200 autopay').autoPay).toBe(false); // income is never autopay
	});
	it('guesses categories from a keyword table', () => {
		expect(guessCategory('Netflix', 'subscription')).toBe('Subscriptions');
		expect(guessCategory('Car insurance', 'bill')).toBe('Insurance');
		expect(guessCategory('Student loan', 'bill')).toBe('Debt');
		expect(guessCategory('Daycare', 'bill')).toBe('Kids & Pets');
		expect(guessCategory('Concert tickets', 'bill')).toBe('Fun');
		expect(guessCategory('Mystery', 'bill')).toBeNull();
		expect(guessCategory('Mystery box', 'subscription')).toBe('Subscriptions');
		expect(guessCategory('Paycheck', 'income')).toBe('Income');
		expect(guessKind('Take home pay')).toBe('income');
	});
	it('a sentence without a name asks for one', () => {
		expect(draftProblems(p('15.49 monthly'))).toEqual(['Add a name, like "Netflix"']);
		expect(draftProblems(p('netflix 15.49'))).toEqual([]);
	});
});

describe('quick add · saving', () => {
	const ids = { categoryId: 'cat-1', accountId: 'acc-1' };
	it('one body for a normal cadence, the name as the first bank name', () => {
		const [body, ...rest] = toRecurringBodies(p('netflix 15.49 on the 7th'), ids);
		expect(rest).toHaveLength(0);
		expect(body).toEqual({
			name: 'Netflix',
			kind: 'subscription',
			amountCents: 1549,
			categoryId: 'cat-1',
			accountId: 'acc-1',
			frequency: 'monthly',
			anchorDate: '2026-11-07',
			endDate: null,
			autoPay: false,
			notes: null,
			url: null,
			bankNames: ['Netflix'],
			amountVaries: false
		});
	});
	it('every 6 months → two yearly items, six months apart', () => {
		const bodies = toRecurringBodies(p('car insurance 216 every 6 months'), ids);
		expect(bodies.map((b) => [b.name, b.frequency, b.anchorDate])).toEqual([
			['Car insurance · Oct', 'yearly', '2026-10-26'],
			['Car insurance · Apr', 'yearly', '2027-04-26']
		]);
	});
	it('twice a month → two monthly items, 15 days apart', () => {
		const d = p('paycheck 2000 twice a month on the 1st');
		expect(secondAnchor(d)).toBe('2026-11-16');
		const bodies = toRecurringBodies(d, ids);
		expect(bodies.map((b) => [b.name, b.anchorDate, b.kind])).toEqual([
			['Paycheck · 1st', '2026-11-01', 'income'],
			['Paycheck · 16th', '2026-11-16', 'income']
		]);
	});
	it('twice a month from a late day wraps into next month', () => {
		expect(secondAnchor({ cadence: 'semimonthly', anchorDate: '2026-10-26' })).toBe('2026-11-11');
	});
	it('nothing to save without an amount or a name', () => {
		expect(toRecurringBodies(p('netflix monthly'), ids)).toEqual([]);
		expect(toRecurringBodies(p('15 monthly'), ids)).toEqual([]);
	});
	it('a one-character name is not used as a bank name', () => {
		expect(toRecurringBodies(p('x 5 monthly'), ids)[0].bankNames).toEqual([]);
	});
	it('the schedule line for the preview and the toast', () => {
		expect(scheduleText(p('netflix 15.49 on the 7th'), TODAY)).toBe(
			'every month · next Sat, Nov 7'
		);
		expect(scheduleText(p('oil change 60 friday'), TODAY)).toBe('one time · Fri, Oct 30');
		expect(scheduleText(p('car insurance 216 every 6 months'), TODAY)).toBe(
			'every 6 months · Mon, Oct 26 and Mon, Apr 26, 2027'
		);
		expect(shortDate('2027-01-05', TODAY)).toBe('Tue, Jan 5, 2027');
	});
});
