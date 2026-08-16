<script lang="ts">
	import type { Snippet } from 'svelte';
	import PanelLeftOpen from '@lucide/svelte/icons/panel-left-open';
	import { page } from '$app/state';
	import * as Sheet from '$lib/components/ui/sheet';
	import { Button } from '$lib/components/ui/button';
	import LibrarySidebar from '$components/app/pages/library/LibrarySidebar.svelte';
	import type { LayoutData } from './$types';

	let { data, children }: { data: LayoutData; children: Snippet } = $props();

	let mobileOpen = $state(false);

	const languageName = $derived((data.languageId ?? 'gsc').toUpperCase());
	const currentFunction = $derived(page.params.func);
</script>

<svelte:head>
	<title>Script API Reference - GSCode</title>
	<meta
		name="description"
		content="A library API page for all the functions available in GSC and CSC."
	/>
	<meta property="og:title" content="Script API Reference - GSCode" />
	<meta property="og:site_name" content="gscode" />
	<meta
		property="og:description"
		content="A reference for all the functions available in GSC and CSC."
	/>
	<meta property="og:image" content="/favicon.png" />
</svelte:head>

<div class="flex min-h-[calc(100svh-3.5rem)] w-full grow items-stretch">
	<!-- Desktop: the index rail is a panel on an edge, sticky under the header bar. -->
	<aside
		class="bg-sidebar border-border sticky top-14 hidden h-[calc(100svh-3.5rem)] w-72 shrink-0 border-r lg:block xl:w-80"
	>
		<LibrarySidebar />
	</aside>

	<div class="flex min-w-0 grow flex-col">
		<!-- Mobile: the same index, in a sheet. -->
		<div
			class="bg-popover border-border sticky top-14 z-30 flex h-12 shrink-0 items-center gap-2 border-b px-3 lg:hidden"
		>
			<Sheet.Root bind:open={mobileOpen}>
				<Sheet.Trigger>
					{#snippet child({ props })}
						<Button variant="ghost" size="icon-sm" aria-label="Open the function index" {...props}>
							<PanelLeftOpen />
						</Button>
					{/snippet}
				</Sheet.Trigger>
				<Sheet.Content side="left" class="bg-sidebar w-[19rem] gap-0 p-0">
					<Sheet.Header class="sr-only">
						<Sheet.Title>Script API index</Sheet.Title>
					</Sheet.Header>
					<div class="flex min-h-0 grow flex-col pt-9">
						<LibrarySidebar onNavigate={() => (mobileOpen = false)} />
					</div>
				</Sheet.Content>
			</Sheet.Root>
			<span class="type-label text-dim">{languageName}</span>
			{#if currentFunction}
				<span aria-hidden="true" class="bg-border h-3.5 w-px"></span>
				<span class="text-muted-foreground truncate font-mono text-xs">{currentFunction}</span>
			{/if}
		</div>

		{@render children()}
	</div>
</div>
