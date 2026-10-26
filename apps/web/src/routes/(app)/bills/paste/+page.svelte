<script lang="ts">
	import type { PageProps } from './$types';
	import AppBar from '#lib/components/AppBar.svelte';
	import NotesImport from '#lib/components/NotesImport.svelte';

	let { data, form }: PageProps = $props();
</script>

<svelte:head><title>Paste from notes · CouplesCash</title></svelte:head>

<AppBar title="Paste from notes" back="/bills" />

<main class="flex flex-col gap-4 px-4 pt-2 pb-6">
	<section class="card border border-base-300/60 bg-base-100 shadow-sm">
		<div class="card-body p-5">
			<NotesImport
				action="?"
				categories={data.categories}
				billsAccountId={data.billsAccountId}
				savingsAccountId={data.savingsAccountId}
				error={form && 'notesError' in form ? (form.notesError ?? null) : null}
			/>
		</div>
	</section>
</main>
