<script lang="ts">
	import { Command as CommandPrimitive } from "bits-ui";
	import SearchIcon from '@lucide/svelte/icons/search';
	import { cn } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		value = $bindable(""),
		...restProps
	}: CommandPrimitive.InputProps = $props();
</script>

<!-- Datum: the palette's search is a flat row under a divider, not a floating pill. -->
<div data-slot="command-input-wrapper" class="border-border flex shrink-0 items-center gap-2.5 border-b px-3.5">
	<SearchIcon class="text-dim size-4 shrink-0" />
	<CommandPrimitive.Input
		bind:ref
		bind:value
		data-slot="command-input"
		class={cn(
			"placeholder:text-dim text-foreground h-9 w-full min-w-0 border-0 bg-transparent font-mono text-xs outline-none disabled:cursor-not-allowed disabled:opacity-50",
			className
		)}
		{...restProps}
	/>
</div>
