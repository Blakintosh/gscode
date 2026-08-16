<script lang="ts">
	/** Raise-coloured strip with an inset edge; children (HudStat) divide with 1px edges. */
	import type { Snippet } from 'svelte';
	import { cn } from '$lib/utils.js';

	let {
		columns,
		class: className = '',
		children
	}: { columns?: number; class?: string; children?: Snippet } = $props();
</script>

<div
	class={cn(
		'inset-edge bg-popover grid overflow-hidden',
		'*:border-border *:border-r *:border-b *:last:border-r-0',
		className
	)}
	style={columns
		? `grid-template-columns:repeat(${columns},minmax(0,1fr))`
		: 'grid-template-columns:repeat(auto-fit,minmax(148px,1fr))'}
>
	{@render children?.()}
</div>
