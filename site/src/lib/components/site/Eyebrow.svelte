<script lang="ts">
	/** Mono uppercase label above a heading or section — the HUD designator. */
	import type { Snippet } from 'svelte';
	import { cn } from '$lib/utils.js';

	let {
		tone = 'primary',
		class: className = '',
		children
	}: { tone?: 'primary' | 'dim' | 'muted'; class?: string; children?: Snippet } = $props();

	const tones = { primary: 'text-primary', dim: 'text-dim', muted: 'text-muted-foreground' };
</script>

<p class={cn('type-label ', tones[tone], className)}>
	{@render children?.()}
</p>
