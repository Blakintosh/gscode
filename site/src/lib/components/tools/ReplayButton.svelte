<script lang="ts">
	/** The quiet "Replay" beside a demo's label: mono, dim, brightens on hover. */
	import type { Snippet } from 'svelte';
	import { cn } from '$lib/utils.js';

	let {
		onclick,
		class: className = '',
		children
	}: { onclick: () => void; class?: string; children?: Snippet } = $props();
</script>

<button
	type="button"
	{onclick}
	class={cn(
		'type-label text-app-dim hover:text-app-text focus-visible:text-app-text -my-2 cursor-pointer py-2 outline-none',
		className
	)}
>
	{#if children}{@render children()}{:else}Replay{/if}
</button>
