import { describe, expect, it } from 'vitest';
import type { RecurringItem } from '#lib/types.ts';
import { legacyRedirect } from './redirects.ts';
import {
	detectPaycheck,
	firstOpenStep,
	funItemBodies,
	funItemName,
	funItemsFor,
	isSetupStep,
	lastWeekdayOnOrBefore,
	nextFirstOfMonth,
	nextStep,
	nextWeekdayOnOrAfter,
	prevStep,
	stepsDone
} from './setup.ts';

describe('steps', () => {
	it('six steps in order', () => {
		expect(isSetupStep('banks')).toBe(true);
		expect(isSetupStep('nope')).toBe(false);
		expect(isSetupStep(null)).toBe(false);
		expect(nextStep('banks')).toBe('roles');
		expect(nextStep('notes')).toBe('partner');
		expect(nextStep('partner')).toBeNull();
		expect(prevStep('banks')).toBeNull();
		expect(prevStep('fun')).toBe('payday');
	});
	it('done flags from the plan', () => {
		const done = stepsDone(
			{
				banksLinked: true,
				rolesSet: true,
				paycheckKnown: false,
				billsAdded: false,
				partnerJoined: false
			},
			{ funSet: true }
		);
		expect(done).toEqual({
			banks: true,
			roles: true,
			payday: false,
			fun: true,
			notes: false,
			partner: false
		});
		expect(firstOpenStep(done)).toBe('payday');
		expect(firstOpenStep(stepsDone(null, { funSet: false }))).toBe('banks');
		expect(
			firstOpenStep({
				banks: true,
				roles: true,
				payday: true,
				fun: true,
				notes: true,
				partner: true
			})
		).toBeNull();
	});
});

describe('weekday dates', () => {
	it('next / last payday (today counts)', () => {
		expect(nextWeekdayOnOrAfter('2026-10-26', 5)).toBe('2026-10-30');
		expect(nextWeekdayOnOrAfter('2026-10-30', 5)).toBe('2026-10-30');
		expect(lastWeekdayOnOrBefore('2026-10-26', 5)).toBe('2026-10-23');
		expect(lastWeekdayOnOrBefore('2026-10-23', 5)).toBe('2026-10-23');
		expect(lastWeekdayOnOrBefore('2026-10-26', 1)).toBe('2026-10-26');
		expect(nextFirstOfMonth('2026-10-26')).toBe('2026-11-01');
		expect(nextFirstOfMonth('2026-12-01')).toBe('2027-01-01');
	});
});

describe('fun money items', () => {
	const opts = { billsAccountId: 'bills', payday: '2026-10-23', today: '2026-10-26' };
	it('four transfer items for two people', () => {
		const bodies = funItemBodies(
			[
				{ memberName: 'Travis', toAccountId: 'f1', weeklyCents: 2000, monthlyCents: 15000 },
				{ memberName: 'Jess Huffman', toAccountId: 'f2', weeklyCents: 2000, monthlyCents: 15000 }
			],
			opts
		);
		expect(bodies.map((b) => [b.name, b.frequency, b.amountCents, b.anchorDate])).toEqual([
			['Fun money · Travis', 'weekly', 2000, '2026-10-23'],
			['Fun money · Travis (monthly)', 'monthly', 15000, '2026-10-01'],
			['Fun money · Jess', 'weekly', 2000, '2026-10-23'],
			['Fun money · Jess (monthly)', 'monthly', 15000, '2026-10-01']
		]);
		for (const b of bodies) {
			expect(b).toMatchObject({ kind: 'transfer', fromAccountId: 'bills', accountId: 'bills' });
		}
		expect(bodies[2].toAccountId).toBe('f2');
	});
	it('zero amounts are left out', () => {
		expect(
			funItemBodies(
				[{ memberName: 'Travis', toAccountId: 'f1', weeklyCents: 0, monthlyCents: 15000 }],
				opts
			).map((b) => b.frequency)
		).toEqual(['monthly']);
		expect(funItemName('Travis', true)).toBe('Fun money · Travis (monthly)');
	});
	it('finds the existing items for an account', () => {
		const item = (over: Partial<RecurringItem>) => ({ kind: 'transfer', ...over }) as RecurringItem;
		const items = [
			item({ id: 'w', toAccountId: 'f1', frequency: 'weekly' }),
			item({ id: 'm', toAccountId: 'f1', frequency: 'monthly' }),
			item({ id: 'x', toAccountId: 'f2', frequency: 'weekly' }),
			item({ id: 'b', kind: 'bill', toAccountId: 'f1', frequency: 'weekly' })
		];
		expect(funItemsFor(items, 'f1')).toMatchObject({ weekly: { id: 'w' }, monthly: { id: 'm' } });
		expect(funItemsFor(items, 'f3')).toEqual({ weekly: null, monthly: null });
	});
});

describe('detectPaycheck', () => {
	const row = (over: Record<string, unknown>) => ({
		amountCents: 245000,
		description: 'ACME CORP PAYROLL',
		payee: null,
		pending: false,
		postedOn: '2026-10-16',
		...over
	});
	it('the latest payroll deposit', () => {
		expect(
			detectPaycheck([
				row({ postedOn: '2026-10-09', amountCents: 240000 }),
				row({}),
				row({ amountCents: -2000, description: 'PAYROLL FEE' })
			])
		).toEqual({ amountCents: 245000, bankName: 'ACME CORP PAYROLL', postedOn: '2026-10-16' });
	});
	it('payee counts; pending and random deposits do not', () => {
		expect(
			detectPaycheck([row({ description: 'ACH CREDIT 123', payee: 'Acme Payroll' })])?.bankName
		).toBe('Acme Payroll');
		expect(detectPaycheck([row({ pending: true })])).toBeNull();
		expect(detectPaycheck([row({ description: 'VENMO CASHOUT' })])).toBeNull();
		expect(detectPaycheck([])).toBeNull();
	});
});

describe('legacyRedirect', () => {
	it('removed screens go to the plan', () => {
		for (const p of [
			'/accounts',
			'/accounts/',
			'/insights',
			'/insights/report',
			'/goals',
			'/goals/new',
			'/goals/abc',
			'/todos',
			'/activity',
			'/expenses/new',
			'/expenses/123',
			'/more',
			'/payperiod',
			'/pending',
			'/transfers'
		])
			expect(legacyRedirect(p), p).toBe('/week');
	});
	it('moved screens', () => {
		expect(legacyRedirect('/calendar')).toBe('/bills');
		expect(legacyRedirect('/inbox')).toBe('/transactions');
		expect(legacyRedirect('/settings/household')).toBe('/settings#household');
		expect(legacyRedirect('/settings/notifications')).toBe('/settings#you');
	});
	it('kept routes stay', () => {
		for (const p of [
			'/week',
			'/accounts/123',
			'/accounts/new',
			'/bills',
			'/bills/new',
			'/settings',
			'/settings/banks',
			'/imports/csv',
			'/goalsetting',
			'/'
		])
			expect(legacyRedirect(p), p).toBeNull();
	});
});
