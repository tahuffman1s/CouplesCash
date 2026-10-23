<script lang="ts">
	import type { Todo } from '#lib/types.ts';
	import { formatMoney } from '#lib/utils/money.ts';
	import { formatDate, relativeDay, relativeTime, today } from '#lib/utils/dates.ts';
	import Icon from './Icon.svelte';

	let {
		todo,
		onToggle,
		onMove,
		onDelete,
		busy = false,
		first = false,
		last = false
	}: {
		todo: Todo;
		onToggle: (todo: Todo) => void;
		/** present → show up/down reorder buttons */
		onMove?: (todo: Todo, dir: -1 | 1) => void;
		onDelete?: (todo: Todo) => void;
		busy?: boolean;
		first?: boolean;
		last?: boolean;
	} = $props();

	const done = $derived(!!todo.doneAt);
	const overdue = $derived(!done && !!todo.dueDate && todo.dueDate < today());
</script>

<li class="flex items-center gap-2 py-2" data-testid="todo-row">
	<label class="flex min-h-11 min-w-11 cursor-pointer items-center justify-center">
		<input
			type="checkbox"
			class="checkbox checkbox-secondary"
			checked={done}
			disabled={busy}
			aria-label={done ? `Mark "${todo.title}" not done` : `Mark "${todo.title}" done`}
			onchange={() => onToggle(todo)}
		/>
	</label>
	<div class="min-w-0 flex-1">
		<p class="truncate font-medium {done ? 'text-base-content/50 line-through' : ''}">
			{todo.title}
		</p>
		<p class="flex flex-wrap items-center gap-x-1.5 text-xs text-base-content/60">
			{#if done && todo.doneAt}
				<span
					>done{todo.doneByName ? ` by ${todo.doneByName}` : ''} · {relativeTime(todo.doneAt)}</span
				>
			{:else}
				{#if todo.amountCents !== null}
					<span class="badge badge-ghost badge-sm tabular">{formatMoney(todo.amountCents)}</span>
				{/if}
				{#if todo.dueDate}
					<span class="badge badge-sm {overdue ? 'badge-outline badge-error' : 'badge-ghost'}">
						{relativeDay(todo.dueDate)} · {formatDate(todo.dueDate)}
					</span>
				{/if}
			{/if}
		</p>
	</div>
	{#if onMove && !done}
		<div class="flex flex-col">
			<button
				type="button"
				class="btn h-6 min-h-6 w-9 btn-ghost btn-xs"
				aria-label="Move {todo.title} up"
				disabled={first || busy}
				onclick={() => onMove(todo, -1)}
			>
				<Icon name="chevron-up" size={16} />
			</button>
			<button
				type="button"
				class="btn h-6 min-h-6 w-9 btn-ghost btn-xs"
				aria-label="Move {todo.title} down"
				disabled={last || busy}
				onclick={() => onMove(todo, 1)}
			>
				<Icon name="chevron-down" size={16} />
			</button>
		</div>
	{/if}
	{#if onDelete}
		<button
			type="button"
			class="btn btn-circle min-h-11 min-w-11 btn-ghost text-base-content/50 btn-sm"
			aria-label="Delete {todo.title}"
			disabled={busy}
			onclick={() => onDelete(todo)}
		>
			<Icon name="trash" size={16} />
		</button>
	{/if}
</li>
