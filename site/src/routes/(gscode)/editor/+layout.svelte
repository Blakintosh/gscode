<script lang="ts">
	import { sineIn } from 'svelte/easing';
	import * as Sidebar from '$components/ui/sidebar/index.js';
	// @ts-ignore
	import PanelLeftOpen from 'lucide-svelte/icons/panel-left-open';
	// @ts-ignore
	import PanelLeftClose from 'lucide-svelte/icons/panel-left-close';

	import EditorSidebar from '$components/app/pages/editor/EditorSidebar.svelte';
	import type { Snippet } from 'svelte';
	import type { LayoutData } from './$types';
	import { setEditorContext } from '$lib/api-editor/editor.svelte';

	let sidebarOpen = $state(true);

	let { data, children }: { data: LayoutData; children: Snippet } = $props();
	setEditorContext(data.editor);
</script>

<svelte:head>
	<title>API Editor - GSCode</title>
	<meta
		name="description"
		content="An editor for the GSCode API - modify and validate GSC and CSC function definitions."
	/>
	<meta property="og:title" content="API Editor - GSCode" />
	<meta property="og:site_name" content="gscode" />
	<meta
		property="og:description"
		content="An editor for the GSCode API - modify and validate function definitions."
	/>
	<meta property="og:image" content="/favicon.png" />
</svelte:head>

<div class="relative grow flex w-full items-stretch overflow-hidden h-full min-h-0">
	<Sidebar.Provider
		bind:open={sidebarOpen}
		style="--sidebar-width: 18rem; --sidebar-width-mobile: 20rem;"
	>
		<EditorSidebar />
		<article
			class="grow overflow-auto bg-article-background/30 pt-12 pb-4 lg:pt-8 lg:pb-8 grid relative"
		>
			<Sidebar.Trigger class="absolute top-4 left-4">
				{#if sidebarOpen}
					<PanelLeftClose />
					<span class="sr-only">Close sidebar</span>
				{:else}
					<PanelLeftOpen />
					<span class="sr-only">Open sidebar</span>
				{/if}
			</Sidebar.Trigger>
			<div class="place-self-center w-full max-w-[1600px] h-full min-h-0">
				{@render children?.()}
			</div>
		</article>
	</Sidebar.Provider>
</div>
