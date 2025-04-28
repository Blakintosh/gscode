<script lang="ts">
	import { sineIn } from 'svelte/easing';
	import * as Sidebar from "$components/ui/sidebar/index.js";
    // @ts-ignore
    import PanelLeftOpen from "lucide-svelte/icons/panel-left-open";
    // @ts-ignore
    import PanelLeftClose from "lucide-svelte/icons/panel-left-close";

	import type { LayoutData } from './$types';
	import LibrarySidebar from '$components/app/pages/library/LibrarySidebar.svelte';
	import type { ScrFunction } from '$lib/models/library';
	export let data: LayoutData;

	let hidden1 = true;
	let transitionParams = {
		x: -320,
		duration: 200,
		easing: sineIn
	};

	let functionSelected: ScrFunction;
	let selectedFunctionLanguageId: string;

	let sidebarOpen = true;

	let loaded: boolean = true;
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

<div class="relative grow flex w-full items-stretch overflow-hidden h-full min-h-0">
	<Sidebar.Provider bind:open={sidebarOpen} style="--sidebar-width: 18rem; --sidebar-width-mobile: 20rem;">
		<LibrarySidebar />
		<article class="grow overflow-auto bg-article-background/30 pt-12 pb-4 lg:pt-8 lg:pb-8 grid relative">
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
				<slot />
			</div>
		</article>
	</Sidebar.Provider>
</div>
