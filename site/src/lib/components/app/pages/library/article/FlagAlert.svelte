<script lang="ts">
	import type { Component } from 'svelte';
	import Brush from '$lib/components/site/Brush.svelte';
	import { cn } from '$lib/utils.js';

	type FlagAlertProps = {
		Icon: Component;
		title: string;
		description: string;
		/** `danger` is reserved for flags that mean the function is unsafe or unusable. */
		tone?: 'info' | 'danger';
	};

	let { Icon, title, description, tone = 'info' }: FlagAlertProps = $props();

	const danger = $derived(tone === 'danger');
</script>

<Brush
	surface="card"
	rim={danger ? 'danger' : 'deep'}
	cut={10}
	bodyClass="flex gap-3 px-4 py-3.5"
	role="note"
>
	<Icon
		class={cn('mt-px size-4 shrink-0', danger ? 'text-destructive' : 'text-muted-foreground')}
	/>
	<div class="min-w-0">
		<p class={cn('type-label', danger ? 'text-destructive' : 'text-primary')}>{title}</p>
		<p class="text-muted-foreground mt-1.5 max-w-[60ch] text-[13.5px] leading-[1.55] font-light">
			{description}
		</p>
	</div>
</Brush>
