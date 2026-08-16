<script lang="ts">
	import Header from '$lib/components/site/SiteHeader.svelte';
	import Footer from '$lib/components/site/SiteFooter.svelte';
	import type { Snippet } from 'svelte';

	let { children }: { children: Snippet } = $props();
</script>

<Header />

<main class="flex w-full grow flex-col">
	{@render children()}
</main>

<Footer />
