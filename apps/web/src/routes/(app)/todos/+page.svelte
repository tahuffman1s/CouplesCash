<script lang="ts">
	import { enhance } from '$app/forms';
	import type { Todo } from '#lib/types.ts';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import NoHousehold from '#lib/components/NoHousehold.svelte';
	import TodoRow from '#lib/components/TodoRow.svelte';
	import { currencySymbol } from '#lib/utils/money.ts';
	import { apiFetch } from '#lib/utils/client.ts';
	import { toasts } from '#lib/state/toasts.svelte.ts';

	let { data, form }: PageProps = $props();

	let todos = $derived<Todo[]>(data.todos ?? []);
	let busy = $state<string | null>(null);
	let adding = $state(false);
	let titleInput = $state<HTMLInputElement | null>(null);
	let deleteForm = $state<HTMLFormElement | null>(null);
	let deleteId = $state('');

	const open = $derived(todos.filter((t) => !t.doneAt).sort((a, b) => a.sortOrder - b.sortOrder));
	const done = $derived(todos.filter((t) => t.doneAt));

	async function onToggle(todo: Todo) {
		if (busy) return;
		const i = todos.findIndex((t) => t.id === todo.id);
		if (i < 0) return;
		const previous = todos[i];
		todos = todos.with(i, {
			...todo,
			doneAt: todo.doneAt ? null : new Date().toISOString(),
			doneBy: todo.doneAt ? null : (data.user?.id ?? null),
			doneByName: todo.doneAt ? null : (data.user?.name ?? null)
		});
		busy = todo.id;
		try {
			const updated = await apiFetch<Todo>(`/todos/${todo.id}/done`, {
				method: todo.doneAt ? 'DELETE' : 'POST'
			});
			todos = todos.map((t) => (t.id === updated.id ? updated : t));
		} catch {
			todos = todos.with(i, previous);
			toasts.show('Could not update.', 'error');
		} finally {
			busy = null;
		}
	}

	async function onMove(todo: Todo, dir: -1 | 1) {
		const ids = open.map((t) => t.id);
		const i = ids.indexOf(todo.id);
		const j = i + dir;
		if (i < 0 || j < 0 || j >= ids.length || busy) return;
		[ids[i], ids[j]] = [ids[j], ids[i]];
		const previous = todos;
		todos = todos.map((t) => {
			const k = ids.indexOf(t.id);
			return k >= 0 ? { ...t, sortOrder: k } : t;
		});
		busy = todo.id;
		try {
			await apiFetch('/todos/order', { method: 'PUT', body: JSON.stringify({ ids }) });
		} catch {
			todos = previous;
			toasts.show('Could not reorder.', 'error');
		} finally {
			busy = null;
		}
	}

	function onDelete(todo: Todo) {
		if (!confirm(`Delete "${todo.title}"?`)) return;
		deleteId = todo.id;
		queueMicrotask(() => deleteForm?.requestSubmit());
	}

	$effect(() => {
		if (form?.added) toasts.show(`"${form.added}" added`, 'success');
		if (form?.deleted) toasts.show('Deleted', 'info');
	});
	$effect(() => {
		if (data.add) titleInput?.focus();
	});
</script>

<svelte:head><title>To pay · CouplesCash</title></svelte:head>

<AppBar title="To pay" subtitle={data.todos ? `${open.length} open` : undefined} back="/more">
	{#snippet action()}
		{#if data.todos}
			<a
				href={data.showDone ? '/todos' : '/todos?done=1'}
				class="btn min-h-11 rounded-full btn-ghost px-3 btn-sm"
				data-testid="toggle-done"
			>
				<Icon name={data.showDone ? 'eye-off' : 'eye'} size={18} />
				{data.showDone ? 'Hide done' : 'Show done'}
			</a>
		{/if}
	{/snippet}
</AppBar>

<main class="flex flex-col gap-4 px-4 pt-2">
	{#if !data.todos}
		<NoHousehold />
	{:else}
		<form
			method="POST"
			action="?/add"
			class="card border border-base-300/60 bg-base-100 shadow-sm"
			use:enhance={() => {
				adding = true;
				return async ({ update }) => {
					await update();
					adding = false;
					titleInput?.focus();
				};
			}}
		>
			<div class="card-body gap-3 p-4">
				{#if form?.error}
					<div class="alert text-sm alert-error" role="alert">{form.error}</div>
				{/if}
				<label class="form-control">
					<span class="sr-only">Title</span>
					<input
						bind:this={titleInput}
						name="title"
						type="text"
						required
						maxlength="120"
						class="input-bordered input min-h-12 w-full"
						placeholder="Transfer $200 to savings"
						aria-label="Title"
						defaultValue={form?.values?.title ?? ''}
					/>
				</label>
				<div class="grid grid-cols-[1fr_1fr_auto] gap-2">
					<label class="input-bordered input flex min-h-12 items-center gap-2">
						<span class="text-base-content/50">{currencySymbol()}</span>
						<input
							name="amount"
							type="text"
							inputmode="decimal"
							class="min-w-0 grow tabular"
							placeholder="Amount"
							aria-label="Amount (optional)"
							defaultValue={form?.values?.amount ?? ''}
						/>
					</label>
					<input
						name="dueDate"
						type="date"
						class="input-bordered input min-h-12 min-w-0"
						aria-label="Due date (optional)"
						defaultValue={form?.values?.dueDate ?? ''}
					/>
					<button class="btn min-h-12 rounded-xl btn-primary" disabled={adding} aria-label="Add">
						{#if adding}<span class="loading loading-sm loading-spinner"></span>{:else}<Icon
								name="plus"
								size={20}
								strokeWidth={2.5}
							/>{/if}
					</button>
				</div>
			</div>
		</form>

		{#if open.length === 0 && done.length === 0}
			<EmptyState
				icon="list-checks"
				title="Nothing on the list"
				body="Shared reminders that aren't bills: transfer to savings, call the insurer, return the parcel. Either of you can tick them off."
			/>
		{:else}
			{#if open.length}
				<section
					class="card border border-base-300/60 bg-base-100 shadow-sm"
					aria-label="Open items"
				>
					<ul class="divide-y divide-base-300/60 px-3" data-testid="open-todos">
						{#each open as todo, i (todo.id)}
							<TodoRow
								{todo}
								{onToggle}
								{onMove}
								{onDelete}
								busy={busy === todo.id}
								first={i === 0}
								last={i === open.length - 1}
							/>
						{/each}
					</ul>
				</section>
			{:else}
				<p class="py-2 text-center text-sm text-base-content/60">All done. Nice.</p>
			{/if}

			{#if data.showDone && done.length}
				<section aria-labelledby="done">
					<h2
						id="done"
						class="mb-1 px-1 text-sm font-semibold tracking-wide text-base-content/70 uppercase"
					>
						Done
					</h2>
					<ul
						class="card divide-y divide-base-300/60 border border-base-300/60 bg-base-100 px-3 shadow-sm"
						data-testid="done-todos"
					>
						{#each done as todo (todo.id)}
							<TodoRow {todo} {onToggle} {onDelete} busy={busy === todo.id} />
						{/each}
					</ul>
				</section>
			{/if}
		{/if}

		<form method="POST" action="?/delete" bind:this={deleteForm} use:enhance class="hidden">
			<input type="hidden" name="id" value={deleteId} />
		</form>
	{/if}
</main>
