<script lang="ts">
	import { page } from '$app/state';
	import Header from '$lib/components/site/SiteHeader.svelte';
	import Footer from '$lib/components/site/SiteFooter.svelte';
	import type { Snippet } from 'svelte';

	let { children }: { children: Snippet } = $props();

	// The library and editor are app surfaces (sidebar + article, no natural end); the
	// footer belongs to the pages that read top to bottom.
	const appSurface = $derived(
		page.url.pathname.startsWith('/library') || page.url.pathname.startsWith('/editor')
	);
</script>

<Header />

<main class="flex w-full grow flex-col">
	{@render children()}
</main>

{#if !appSurface}
	<Footer />
{/if}
