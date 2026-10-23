<script lang="ts">
	import type { Account, AccountType, ExternalAccount } from '#lib/types.ts';
	import { ACCOUNT_TYPES } from '#lib/types.ts';
	import { ACCOUNT_TYPE_LABEL, guessAccountType } from '#lib/utils/labels.ts';
	import { apiFetch } from '#lib/utils/client.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';
	import type { ExternalAccountMapping } from '#lib/server/api.ts';
	import MoneyText from './MoneyText.svelte';
	import Icon from './Icon.svelte';

	let {
		ext,
		accounts,
		onMapped
	}: {
		ext: ExternalAccount;
		accounts: Account[];
		/** server version after a successful mapping change */
		onMapped?: (next: ExternalAccount, created: boolean) => void;
	} = $props();

	const CREATE = '__create';
	let selected = $derived(ext.accountId ?? '');
	let creating = $state(false);
	let busy = $state(false);
	let newName = $state('');
	let newType = $state<AccountType>('checking');

	const groups = $derived(
		ACCOUNT_TYPES.map((type) => ({
			type,
			label: ACCOUNT_TYPE_LABEL[type],
			items: accounts.filter((a) => a.type === type)
		})).filter((g) => g.items.length)
	);
	const label = $derived(`${ext.orgName} · ${ext.name}`);

	async function save(body: ExternalAccountMapping, optimistic: string | null) {
		const previous = selected;
		selected = optimistic ?? '';
		busy = true;
		try {
			const next = await apiFetch<ExternalAccount>(`/banks/accounts/${ext.id}`, {
				method: 'PUT',
				body: JSON.stringify(body)
			});
			selected = next.accountId ?? '';
			onMapped?.(next, 'create' in body);
			toasts.show(
				next.accountId
					? `${ext.name} now updates ${next.accountName ?? 'the account'}`
					: `${ext.name} is no longer tracked`,
				'success'
			);
			creating = false;
		} catch {
			selected = previous;
			toasts.show('Could not change the mapping.', 'error');
		} finally {
			busy = false;
		}
	}

	function onChange(e: Event) {
		const value = (e.currentTarget as HTMLSelectElement).value;
		if (value === CREATE) {
			newName = ext.name;
			newType = guessAccountType(ext.name);
			creating = true;
			// keep the select on its previous value until the account exists
			(e.currentTarget as HTMLSelectElement).value = selected;
			return;
		}
		void save({ accountId: value || null }, value || null);
	}

	function create(e: SubmitEvent) {
		e.preventDefault();
		const name = newName.trim();
		if (!name) return;
		void save({ create: { name, type: newType, institution: ext.orgName } }, null);
	}
</script>

<li class="flex flex-col gap-2 py-3" data-testid="external-account" data-external={ext.externalId}>
	<div class="flex items-center gap-3">
		<span
			class="flex h-9 w-9 shrink-0 items-center justify-center rounded-xl bg-base-200 text-base-content/70"
			aria-hidden="true"
		>
			<Icon name={guessAccountType(ext.name) === 'credit' ? 'credit-card' : 'landmark'} size={18} />
		</span>
		<div class="min-w-0 flex-1">
			<p class="truncate text-sm font-medium">{ext.name}</p>
			<p class="truncate text-xs text-base-content/60">{ext.orgName}</p>
		</div>
		<MoneyText cents={ext.balanceCents} class="text-sm font-semibold" tone="auto" />
	</div>
	<label class="form-control">
		<span class="sr-only">Mapped account for {label}</span>
		<select
			class="select-bordered select min-h-11 w-full text-sm select-sm"
			value={selected}
			disabled={busy}
			aria-label="Mapped account for {label}"
			onchange={onChange}
		>
			<option value="">Not tracked</option>
			{#each groups as g (g.type)}
				<optgroup label={g.label}>
					{#each g.items as a (a.id)}
						<option value={a.id}>{a.name}</option>
					{/each}
				</optgroup>
			{/each}
			<option value={CREATE}>Create account…</option>
		</select>
	</label>
	{#if creating}
		<form class="flex flex-col gap-2 rounded-xl bg-base-200/70 p-3" onsubmit={create}>
			<p class="text-xs font-medium text-base-content/70">New account from {ext.orgName}</p>
			<div class="grid grid-cols-2 gap-2">
				<label class="form-control">
					<span class="sr-only">Account name</span>
					<input
						type="text"
						required
						maxlength="60"
						class="input-bordered input min-h-11 w-full text-sm input-sm"
						aria-label="New account name"
						bind:value={newName}
					/>
				</label>
				<label class="form-control">
					<span class="sr-only">Account type</span>
					<select
						class="select-bordered select min-h-11 w-full text-sm select-sm"
						aria-label="New account type"
						bind:value={newType}
					>
						{#each ACCOUNT_TYPES as t (t)}
							<option value={t}>{ACCOUNT_TYPE_LABEL[t]}</option>
						{/each}
					</select>
				</label>
			</div>
			<div class="flex gap-2">
				<button class="btn min-h-11 flex-1 rounded-full btn-primary btn-sm" disabled={busy}>
					{#if busy}<span class="loading loading-xs loading-spinner"></span>{/if}
					Create &amp; map
				</button>
				<button
					type="button"
					class="btn min-h-11 rounded-full btn-ghost btn-sm"
					onclick={() => (creating = false)}>Cancel</button
				>
			</div>
		</form>
	{/if}
</li>
