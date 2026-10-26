<script lang="ts">
	import { untrack } from 'svelte';
	import { invalidateAll } from '$app/navigation';
	import type { CsvImportResult } from '#lib/types.ts';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import { apiFetch, ClientApiError } from '#lib/utils/client.ts';
	import { formatMoney } from '#lib/utils/money.ts';
	import { formatTimestamp } from '#lib/utils/dates.ts';
	import { signedAmount } from '#lib/utils/labels.ts';
	import {
		buildRows,
		columnLabels,
		DATE_FORMAT_LABEL,
		detectDateFormat,
		detectHeader,
		guessMapping,
		MAX_FILE_BYTES,
		MAX_ROWS,
		parseDate,
		type DateFormat,
		type Mapping,
		type Row
	} from '#lib/utils/csv.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data }: PageProps = $props();

	type Step = 'pick' | 'map' | 'done';
	let step = $state<Step>('pick');
	let accountId = $state(untrack(() => data.accounts[0]?.id ?? ''));
	let filename = $state('');
	let fileError = $state('');
	let parsing = $state(false);
	let rows = $state<Row[]>([]);
	let hasHeader = $state(true);
	let mapping = $state<Mapping>({
		date: null,
		description: null,
		amount: null,
		debit: null,
		credit: null,
		payee: null,
		memo: null
	});
	let amountMode = $state<'single' | 'split'>('single');
	let outflowsNegative = $state(true);
	let dateFormat = $state<DateFormat>('iso');
	let detectedFormat = $state<DateFormat | null>(null);
	let importing = $state(false);
	let result = $state<CsvImportResult | null>(null);
	let clientSkipped = $state(0);

	const headers = $derived(hasHeader ? (rows[0] ?? []) : null);
	const width = $derived(Math.max(0, ...rows.slice(0, 50).map((r) => r.length)));
	const labels = $derived(columnLabels(headers, hasHeader ? rows.slice(1) : rows, width));
	const dataRows = $derived(hasHeader ? rows.slice(1) : rows);
	const preview = $derived(dataRows.slice(0, 10));
	const effectiveMapping = $derived<Mapping>(
		amountMode === 'single'
			? { ...mapping, debit: null, credit: null }
			: { ...mapping, amount: null }
	);
	const built = $derived(
		buildRows(rows, { mapping: effectiveMapping, dateFormat, outflowsNegative, hasHeader })
	);
	const dateSample = $derived.by(() => {
		if (mapping.date === null) return '';
		const cell = dataRows.find((r) => (r[mapping.date!] ?? '').trim())?.[mapping.date] ?? '';
		const parsed = parseDate(cell, dateFormat);
		return cell ? `“${cell.trim()}” → ${parsed ?? 'not recognised'}` : '';
	});
	const ready = $derived(
		!!accountId &&
			mapping.date !== null &&
			mapping.description !== null &&
			(amountMode === 'single'
				? mapping.amount !== null
				: mapping.debit !== null || mapping.credit !== null) &&
			built.rows.length > 0
	);

	async function onFile(e: Event) {
		const input = e.currentTarget as HTMLInputElement;
		const file = input.files?.[0];
		fileError = '';
		if (!file) return;
		if (file.size > MAX_FILE_BYTES) {
			fileError = 'That file is over 2 MB. Export a shorter date range and try again.';
			input.value = '';
			return;
		}
		parsing = true;
		try {
			const text = await file.text();
			const Papa = (await import('papaparse')).default;
			const parsed = Papa.parse<string[]>(text, { skipEmptyLines: 'greedy' });
			const all = parsed.data.map((r) => r.map((c) => (c ?? '').toString()));
			if (all.length === 0) {
				fileError = 'The file looks empty.';
				return;
			}
			rows = all;
			filename = file.name;
			hasHeader = detectHeader(all);
			const body = hasHeader ? all.slice(1) : all;
			mapping = guessMapping(hasHeader ? all[0] : null, body);
			amountMode =
				mapping.amount === null && (mapping.debit !== null || mapping.credit !== null)
					? 'split'
					: 'single';
			detectedFormat =
				mapping.date === null
					? null
					: detectDateFormat(body.slice(0, 20).map((r) => r[mapping.date!] ?? ''));
			dateFormat = detectedFormat ?? 'iso';
			outflowsNegative = true;
			step = 'map';
		} catch {
			fileError = "Couldn't read that file. Is it a CSV export?";
		} finally {
			parsing = false;
			input.value = '';
		}
	}

	function onHeaderToggle() {
		const body = hasHeader ? rows.slice(1) : rows;
		mapping = guessMapping(hasHeader ? rows[0] : null, body);
		amountMode =
			mapping.amount === null && (mapping.debit !== null || mapping.credit !== null)
				? 'split'
				: 'single';
	}

	async function doImport() {
		if (!ready || importing) return;
		importing = true;
		try {
			clientSkipped = built.skipped;
			result = await apiFetch<CsvImportResult>('/imports/csv', {
				method: 'POST',
				body: JSON.stringify({ accountId, filename, rows: built.rows })
			});
			step = 'done';
			void invalidateAll();
		} catch (e) {
			toasts.show(
				e instanceof ClientApiError && e.problem.detail
					? e.problem.detail
					: 'Import failed. Check your connection and try again.',
				'error'
			);
		} finally {
			importing = false;
		}
	}

	function reset() {
		step = 'pick';
		rows = [];
		result = null;
		filename = '';
	}

	const FIELD_LABEL: Record<keyof Mapping, string> = {
		date: 'Date',
		description: 'Description',
		amount: 'Amount (signed)',
		debit: 'Debit (money out)',
		credit: 'Credit (money in)',
		payee: 'Payee (optional)',
		memo: 'Memo (optional)'
	};
	const visibleFields = $derived<(keyof Mapping)[]>(
		amountMode === 'single'
			? ['date', 'description', 'amount', 'payee', 'memo']
			: ['date', 'description', 'debit', 'credit', 'payee', 'memo']
	);
	const accountName = $derived(data.accounts.find((a) => a.id === accountId)?.name ?? '');
</script>

<svelte:head><title>Import CSV · CouplesCash</title></svelte:head>

<AppBar
	title="Import CSV"
	back="/settings#banks"
	subtitle={step === 'map' ? filename : undefined}
/>

<main class="flex flex-col gap-4 px-4 pt-2">
	{#if data.accounts.length === 0}
		<EmptyState
			icon="file-spreadsheet"
			title="Add an account first"
			body="Imported transactions belong to one of your accounts, so create the account the statement came from before uploading."
			cta="Add an account"
			href="/accounts/new"
		/>
	{:else if step === 'pick'}
		<section class="card border border-base-300/60 bg-base-100 shadow-sm" aria-labelledby="pick">
			<div class="card-body gap-4 p-5">
				<h2 id="pick" class="font-semibold">1 · Choose the file</h2>
				<p class="text-sm text-base-content/70">
					Export a CSV from your bank's website (most offer it under transactions or statements) and
					pick the matching CouplesCash account. Nothing is uploaded until you review the columns.
				</p>
				<label class="form-control">
					<span class="label-text mb-1 text-sm font-medium">Account</span>
					<select class="select-bordered select min-h-12 w-full" bind:value={accountId}>
						{#each data.accounts as a (a.id)}
							<option value={a.id}>{a.name} · {a.institution}</option>
						{/each}
					</select>
				</label>
				<label class="form-control">
					<span class="label-text mb-1 text-sm font-medium">CSV file</span>
					<input
						type="file"
						accept=".csv,.txt,text/csv,text/plain"
						class="file-input-bordered file-input min-h-12 w-full"
						aria-label="CSV file"
						disabled={parsing}
						onchange={onFile}
					/>
					<span class="label-text-alt mt-1 text-base-content/60">Up to 2 MB · {MAX_ROWS} rows</span>
				</label>
				{#if fileError}<p class="text-sm text-error" role="alert">{fileError}</p>{/if}
				{#if parsing}<span class="loading loading-sm loading-spinner"></span>{/if}
			</div>
		</section>

		{#if data.recent.length}
			<section
				class="card border border-base-300/60 bg-base-100 shadow-sm"
				aria-labelledby="recent"
			>
				<div class="card-body gap-2 p-5">
					<h2 id="recent" class="font-semibold">Recent imports</h2>
					<ul class="divide-y divide-base-300/60 text-sm">
						{#each data.recent as r (r.id)}
							<li class="flex items-center gap-3 py-2">
								<Icon name="file-spreadsheet" size={18} class="shrink-0 text-base-content/50" />
								<div class="min-w-0 flex-1">
									<p class="truncate">{r.filename}</p>
									<p class="text-xs text-base-content/50">
										{formatTimestamp(r.createdAt)} · {r.createdByName}
									</p>
								</div>
								<p class="text-right text-xs text-base-content/60">
									{r.imported} new<br />{r.duplicates} dup{r.duplicates === 1 ? '' : 's'}
								</p>
							</li>
						{/each}
					</ul>
				</div>
			</section>
		{/if}
	{:else if step === 'map'}
		<section class="card border border-base-300/60 bg-base-100 shadow-sm" aria-labelledby="map">
			<div class="card-body gap-4 p-5">
				<h2 id="map" class="font-semibold">2 · Match the columns</h2>
				<label class="label cursor-pointer justify-start gap-3 px-0">
					<input
						type="checkbox"
						class="toggle toggle-secondary toggle-sm"
						bind:checked={hasHeader}
						onchange={onHeaderToggle}
					/>
					<span class="label-text text-sm">First row is a header</span>
				</label>

				<div class="form-control">
					<span class="label-text mb-1 text-sm font-medium">Amount columns</span>
					<div class="join w-full" role="radiogroup" aria-label="Amount columns">
						<button
							type="button"
							role="radio"
							aria-checked={amountMode === 'single'}
							class="btn join-item min-h-11 flex-1 btn-sm {amountMode === 'single'
								? 'btn-neutral'
								: 'border border-base-300 btn-ghost'}"
							onclick={() => (amountMode = 'single')}>One signed amount</button
						>
						<button
							type="button"
							role="radio"
							aria-checked={amountMode === 'split'}
							class="btn join-item min-h-11 flex-1 btn-sm {amountMode === 'split'
								? 'btn-neutral'
								: 'border border-base-300 btn-ghost'}"
							onclick={() => (amountMode = 'split')}>Debit + credit</button
						>
					</div>
				</div>

				{#each visibleFields as field (field)}
					<label class="form-control">
						<span class="label-text mb-1 text-sm font-medium">{FIELD_LABEL[field]}</span>
						<select
							class="select-bordered select min-h-12 w-full"
							aria-label={FIELD_LABEL[field]}
							value={mapping[field] === null ? '' : String(mapping[field])}
							onchange={(e) => {
								const v = (e.currentTarget as HTMLSelectElement).value;
								mapping = { ...mapping, [field]: v === '' ? null : Number(v) };
							}}
						>
							<option value="">—</option>
							{#each labels as l, i (i)}
								<option value={String(i)}>{l}</option>
							{/each}
						</select>
					</label>
				{/each}

				{#if amountMode === 'single'}
					<div class="form-control">
						<span class="label-text mb-1 text-sm font-medium">Sign convention</span>
						<div class="join w-full" role="radiogroup" aria-label="Sign convention">
							<button
								type="button"
								role="radio"
								aria-checked={outflowsNegative}
								class="btn join-item min-h-11 flex-1 btn-sm {outflowsNegative
									? 'btn-neutral'
									: 'border border-base-300 btn-ghost'}"
								onclick={() => (outflowsNegative = true)}>Outflows are negative</button
							>
							<button
								type="button"
								role="radio"
								aria-checked={!outflowsNegative}
								class="btn join-item min-h-11 flex-1 btn-sm {!outflowsNegative
									? 'btn-neutral'
									: 'border border-base-300 btn-ghost'}"
								onclick={() => (outflowsNegative = false)}>Outflows are positive</button
							>
						</div>
					</div>
				{/if}

				<label class="form-control">
					<span class="label-text mb-1 text-sm font-medium">Date format</span>
					<select
						class="select-bordered select min-h-12 w-full"
						bind:value={dateFormat}
						aria-label="Date format"
					>
						{#each Object.entries(DATE_FORMAT_LABEL) as [v, l] (v)}
							<option value={v}>{l}{detectedFormat === v ? ' · detected' : ''}</option>
						{/each}
					</select>
					{#if dateSample}
						<span class="label-text-alt mt-1 text-base-content/60" data-testid="date-sample"
							>{dateSample}</span
						>
					{/if}
				</label>
			</div>
		</section>

		<section class="card border border-base-300/60 bg-base-100 shadow-sm" aria-labelledby="preview">
			<div class="card-body gap-3 p-5">
				<div class="flex items-baseline justify-between">
					<h2 id="preview" class="font-semibold">Preview</h2>
					<p class="text-xs text-base-content/60" data-testid="preview-count">
						{built.rows.length} ready{built.skipped ? ` · ${built.skipped} skipped` : ''}
					</p>
				</div>
				<div class="overflow-x-auto">
					<table class="table table-xs">
						<thead>
							<tr>
								<th>Date</th>
								<th>Description</th>
								<th class="text-right">Amount</th>
							</tr>
						</thead>
						<tbody>
							{#each preview as r, i (i)}
								{@const b = buildRows([r], {
									mapping: effectiveMapping,
									dateFormat,
									outflowsNegative,
									hasHeader: false
								}).rows[0]}
								<tr class={b ? '' : 'opacity-50'}>
									{#if b}
										<td class="whitespace-nowrap tabular">{b.postedOn}</td>
										<td class="max-w-[12rem] truncate">{b.payee ?? b.description}</td>
										<td
											class="text-right tabular {b.amountCents < 0 ? 'text-error' : 'text-success'}"
											>{signedAmount(b.amountCents, formatMoney)}</td
										>
									{:else}
										<td colspan="3" class="truncate text-base-content/50">
											Skipped: {r.filter(Boolean).join(' · ').slice(0, 60)}
										</td>
									{/if}
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
				<div class="flex gap-2">
					<button
						type="button"
						class="btn min-h-12 flex-1 rounded-full btn-primary"
						disabled={!ready || importing}
						onclick={doImport}
					>
						{#if importing}<span class="loading loading-sm loading-spinner"></span>{:else}<Icon
								name="upload"
								size={18}
							/>{/if}
						Import {built.rows.length} row{built.rows.length === 1 ? '' : 's'} into {accountName}
					</button>
					<button type="button" class="btn min-h-12 rounded-full btn-ghost" onclick={reset}
						>Back</button
					>
				</div>
			</div>
		</section>
	{:else if result}
		<section class="card border border-base-300/60 bg-base-100 shadow-sm" aria-labelledby="done">
			<div class="card-body gap-4 p-5" data-testid="import-result">
				<h2 id="done" class="flex items-center gap-2 font-semibold">
					<Icon name="circle-check" size={20} class="text-success" /> Imported {filename}
				</h2>
				<div class="grid grid-cols-3 gap-2 text-center">
					<div class="rounded-xl bg-base-200 p-3">
						<p class="text-2xl font-bold tabular" data-testid="result-imported">
							{result.imported}
						</p>
						<p class="text-xs text-base-content/60">imported</p>
					</div>
					<div class="rounded-xl bg-base-200 p-3">
						<p class="text-2xl font-bold tabular" data-testid="result-duplicates">
							{result.duplicates}
						</p>
						<p class="text-xs text-base-content/60">duplicates</p>
					</div>
					<div class="rounded-xl bg-base-200 p-3">
						<p class="text-2xl font-bold tabular" data-testid="result-skipped">
							{result.skipped + clientSkipped}
						</p>
						<p class="text-xs text-base-content/60">skipped</p>
					</div>
				</div>
				<p class="text-sm text-base-content/70">
					{result.inboxPending} transaction{result.inboxPending === 1 ? '' : 's'} now wait in To review.
					Each one gets a suggested category or bill; nothing changes until you confirm.
				</p>
				<div class="flex gap-2">
					<a href="/transactions" class="btn min-h-12 flex-1 rounded-full btn-primary">
						<Icon name="inbox" size={18} /> Review them
					</a>
					<button type="button" class="btn min-h-12 rounded-full btn-ghost" onclick={reset}
						>Import another</button
					>
				</div>
			</div>
		</section>
	{/if}
</main>
