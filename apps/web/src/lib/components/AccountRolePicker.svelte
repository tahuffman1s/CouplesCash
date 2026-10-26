<script lang="ts">
	import { untrack } from 'svelte';
	import type { AccountRole, Member } from '#lib/types.ts';
	import { ACCOUNT_ROLES, ROLE_LABEL } from '#lib/types.ts';
	import type { RoleChoice, RoleRowView } from '#lib/utils/roles.ts';
	import { formatMoney } from '#lib/utils/money.ts';
	import { firstName } from '#lib/utils/plan.ts';
	import Icon from './Icon.svelte';

	/**
	 * v7 "Which account is which": one row per account (and per bank account not tracked yet) with a
	 * role and, for fun money, whose it is. Only one Bills account: picking it elsewhere moves it.
	 * Lives inside the caller's <form> (posts `row`, `role_<key>`, `owner_<key>`, `name_`/`org_`).
	 */
	let {
		rows,
		defaults,
		members
	}: {
		rows: RoleRowView[];
		defaults: Record<string, RoleChoice>;
		members: Member[];
	} = $props();

	let choices = $state<Record<string, RoleChoice>>(untrack(() => structuredClone(defaults)));

	const HINT: Record<AccountRole, string> = {
		bills: 'The one the weekly math runs on',
		savings: 'Gets the leftover; shows savings bills',
		fun: 'Personal money, never reviewed',
		other: 'Not in the math'
	};

	function pick(key: string, role: AccountRole) {
		const next = { ...choices };
		if (role === 'bills')
			for (const k of Object.keys(next))
				if (k !== key && next[k].role === 'bills') next[k] = { role: 'other', ownerUserId: null };
		const owner =
			role === 'fun'
				? (next[key]?.ownerUserId ??
					members.find((m) => !Object.values(next).some((c) => c.ownerUserId === m.id))?.id ??
					null)
				: null;
		next[key] = { role, ownerUserId: owner };
		choices = next;
	}
</script>

<ul class="flex flex-col gap-2" data-testid="role-picker">
	{#each rows as r (r.key)}
		{@const c = choices[r.key] ?? { role: 'other', ownerUserId: null }}
		<li
			class="rounded-2xl border border-base-300/70 p-3 {c.role === 'bills'
				? 'border-primary/50 bg-primary/5'
				: ''}"
			data-testid="role-row"
			data-name={r.name}
		>
			<input type="hidden" name="row" value={r.key} />
			<input type="hidden" name="role_{r.key}" value={c.role} />
			{#if r.key.startsWith('x:')}
				<input type="hidden" name="name_{r.key}" value={r.name} />
				<input type="hidden" name="org_{r.key}" value={r.org} />
			{/if}
			<div class="flex items-center gap-3">
				<span
					class="flex h-9 w-9 shrink-0 items-center justify-center rounded-xl bg-base-200 text-base-content/70"
					aria-hidden="true"
				>
					<Icon
						name={c.role === 'savings' ? 'piggy-bank' : c.role === 'fun' ? 'wallet' : 'landmark'}
						size={18}
					/>
				</span>
				<div class="min-w-0 flex-1">
					<p class="truncate text-sm font-medium">{r.name}</p>
					<p class="truncate text-xs text-base-content/60">{r.org}</p>
				</div>
				<span class="shrink-0 font-mono text-sm tabular">{formatMoney(r.balanceCents)}</span>
			</div>
			<div class="mt-2 grid grid-cols-4 gap-1" role="radiogroup" aria-label="What {r.name} is for">
				{#each ACCOUNT_ROLES as role (role)}
					<button
						type="button"
						role="radio"
						aria-checked={c.role === role}
						class="btn h-auto min-h-11 rounded-xl px-1 text-xs leading-tight font-medium {c.role ===
						role
							? 'btn-primary'
							: 'border border-base-300 btn-ghost'}"
						onclick={() => pick(r.key, role)}>{ROLE_LABEL[role]}</button
					>
				{/each}
			</div>
			<p class="mt-1 text-xs text-base-content/55">{HINT[c.role]}</p>
			{#if c.role === 'fun'}
				<label class="mt-2 flex items-center gap-2 text-sm">
					<span class="shrink-0 text-base-content/70">Whose?</span>
					<select
						name="owner_{r.key}"
						class="select-bordered select min-h-11 flex-1 select-sm"
						aria-label="Whose fun money is {r.name}"
						value={c.ownerUserId ?? ''}
						onchange={(e) =>
							(choices = {
								...choices,
								[r.key]: { role: 'fun', ownerUserId: e.currentTarget.value || null }
							})}
					>
						<option value="">Not decided</option>
						{#each members as m (m.id)}
							<option value={m.id}>{firstName(m.displayName) ?? m.displayName}</option>
						{/each}
					</select>
				</label>
			{/if}
		</li>
	{/each}
</ul>
