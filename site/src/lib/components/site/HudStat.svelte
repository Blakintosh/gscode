<script lang="ts">
	/**
	 * The HUD pattern: mono label in dim above, Chakra Petch value below. Sits in a raise
	 * strip with an inset edge; siblings divide with 1px edges — see HudStrip.
	 */
	import type { Snippet } from 'svelte';
	import { cn } from '$lib/utils.js';

	let {
		label,
		value,
		tone = 'default',
		size = 'md',
		sub,
		class: className = '',
		children
	}: {
		label: string;
		value?: string | number;
		tone?: 'default' | 'primary' | 'destructive';
		size?: 'sm' | 'md' | 'lg';
		sub?: string;
		class?: string;
		children?: Snippet;
	} = $props();

	const tones = { default: '', primary: 'text-primary', destructive: 'text-destructive' };
	const sizes = { sm: 'text-[15px]', md: 'text-[17px]', lg: 'text-[24px]' };
</script>

<div class={cn('min-w-0 px-5 py-4', className)}>
	<span class="type-label block text-[11px] tracking-[.19em] text-dim">{label}</span>
	<b
		class={cn(
			'type-display mt-1.5 block truncate leading-none tracking-[.02em] tabular-nums',
			sizes[size],
			tones[tone]
		)}
	>
		{#if children}{@render children()}{:else}{value}{/if}
	</b>
	{#if sub}
		<span class="mt-1 block font-mono text-[12px] text-dim">{sub}</span>
	{/if}
</div>
