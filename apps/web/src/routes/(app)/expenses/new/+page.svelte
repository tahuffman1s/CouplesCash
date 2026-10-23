<script lang="ts">
	import { enhance } from '$app/forms';
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import ExpenseForm from '#lib/components/ExpenseForm.svelte';

	let { data, form }: PageProps = $props();
	let submitting = $state(false);
</script>

<svelte:head><title>New expense · CouplesCash</title></svelte:head>

<AppBar title="Add an expense" back="/bills?tab=expenses" />

<main class="px-4 pt-2">
	<form
		method="POST"
		class="card border border-base-300/60 bg-base-100 shadow-sm"
		use:enhance={() => {
			submitting = true;
			return async ({ update }) => {
				await update();
				submitting = false;
			};
		}}
	>
		<div class="card-body gap-4 p-5">
			<ExpenseForm
				categories={data.categories}
				accounts={data.accounts}
				defaultAccountId={data.household?.defaultAccountId ?? null}
				values={form?.values}
				error={form?.error}
			/>
			<button class="btn mt-2 min-h-12 rounded-full btn-primary" disabled={submitting}>
				{#if submitting}<span class="loading loading-sm loading-spinner"></span>{/if}
				Save expense
			</button>
		</div>
	</form>
</main>
