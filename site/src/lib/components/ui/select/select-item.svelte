<script lang="ts">
	import { Select as SelectPrimitive } from "bits-ui";
	import { cn, type WithoutChild } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		value,
		label,
		children: childrenProp,
		...restProps
	}: WithoutChild<SelectPrimitive.ItemProps> = $props();
</script>

<!-- Datum: a menu item is selected by a 2px left border and a 9% teal wash, never a pill. -->
<SelectPrimitive.Item
	bind:ref
	{value}
	data-slot="select-item"
	class={cn(
		"text-muted-foreground relative flex w-full cursor-pointer items-center gap-2 border-l-2 border-transparent py-2 pr-9 pl-4 transition-colors outline-hidden select-none",
		"data-highlighted:text-foreground data-highlighted:bg-[var(--wash-hover)]",
		"data-selected:text-primary data-selected:border-primary data-selected:bg-[var(--wash-active)]",
		"data-[disabled]:pointer-events-none data-[disabled]:opacity-50 [&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4",
		"*:[span]:last:flex *:[span]:last:items-center *:[span]:last:gap-2",
		className
	)}
	{...restProps}
>
	{#snippet children({ selected, highlighted })}
		<span class="absolute end-4 top-1/2 flex size-1.5 -translate-y-1/2 items-center justify-center">
			{#if selected}
				<span class="bg-primary block size-1.5"></span>
			{/if}
		</span>
		<span class="flex flex-1 shrink-0 gap-2 whitespace-nowrap">
			{#if childrenProp}
				{@render childrenProp({ selected, highlighted })}
			{:else}
				{label || value}
			{/if}
		</span>
	{/snippet}
</SelectPrimitive.Item>
