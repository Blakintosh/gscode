<script lang="ts">
	import type { Snippet } from 'svelte';
	import PanelLeftOpen from '@lucide/svelte/icons/panel-left-open';
	import { page } from '$app/state';
	import * as Sheet from '$lib/components/ui/sheet';
	import { Button } from '$lib/components/ui/button';
	import MacroSidebar from '$components/app/pages/library/MacroSidebar.svelte';
	import type { LayoutData } from './$types';

	let { data, children }: { data: LayoutData; children: Snippet } = $props();

	let mobileOpen = $state(false);

	const currentMacro = $derived(page.params.macro);
	const title = $derived(`${data.game.shortName} Macro Reference - GSCode`);
	const description = $derived(
		`Every stock preprocessor macro defined by the .gsh headers in the ${data.game.name} mod tools.`
	);
</script>

<svelte:head>
	<title>{title}</title>
	<meta name="description" content={description} />
	<meta property="og:title" content={title} />
	<meta property="og:site_name" content="gscode" />
	<meta property="og:description" content={description} />
	<meta property="og:image" content="/favicon.png" />
</svelte:head>

<!-- An app frame: the page never scrolls; the index and the article each scroll in their own pane. -->
<div class="flex h-[calc(100svh-3.5rem)] w-full items-stretch overflow-hidden">
	<aside class="bg-sidebar border-border hidden h-full w-72 shrink-0 border-r lg:block xl:w-80">
		<MacroSidebar />
	</aside>

	<div class="flex min-w-0 grow flex-col overflow-y-auto">
		<!-- Mobile: the same index, in a sheet. -->
		<div
			class="bg-popover border-border sticky top-0 z-30 flex h-12 shrink-0 items-center gap-2 border-b px-3 lg:hidden"
		>
			<Sheet.Root bind:open={mobileOpen}>
				<Sheet.Trigger>
					{#snippet child({ props })}
						<Button variant="ghost" size="icon-sm" aria-label="Open the macro index" {...props}>
							<PanelLeftOpen />
						</Button>
					{/snippet}
				</Sheet.Trigger>
				<Sheet.Content side="left" class="bg-sidebar w-[19rem] gap-0 p-0">
					<Sheet.Header class="sr-only">
						<Sheet.Title>Macro index</Sheet.Title>
					</Sheet.Header>
					<div class="flex min-h-0 grow flex-col pt-9">
						<MacroSidebar onNavigate={() => (mobileOpen = false)} />
					</div>
				</Sheet.Content>
			</Sheet.Root>
			<span class="type-label text-dim">{data.game.shortName} GSH</span>
			{#if currentMacro}
				<span aria-hidden="true" class="bg-border h-3.5 w-px"></span>
				<span class="text-muted-foreground truncate font-mono text-xs">{currentMacro}</span>
			{/if}
		</div>

		{@render children()}
	</div>
</div>
