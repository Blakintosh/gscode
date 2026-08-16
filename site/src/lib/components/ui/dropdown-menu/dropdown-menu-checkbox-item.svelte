<script lang="ts">
	import { DropdownMenu as DropdownMenuPrimitive } from "bits-ui";
	import { cn, type WithoutChildrenOrChild } from "$lib/utils.js";
	import type { Snippet } from "svelte";

	let {
		ref = $bindable(null),
		checked = $bindable(false),
		indeterminate = $bindable(false),
		class: className,
		children: childrenProp,
		...restProps
	}: WithoutChildrenOrChild<DropdownMenuPrimitive.CheckboxItemProps> & {
		children?: Snippet;
	} = $props();
</script>

<DropdownMenuPrimitive.CheckboxItem
	bind:ref
	bind:checked
	bind:indeterminate
	data-slot="dropdown-menu-checkbox-item"
	class={cn(
		"text-muted-foreground relative flex cursor-pointer items-center gap-2 border-l-2 border-transparent py-2 pr-9 pl-4 transition-colors outline-hidden select-none",
		"data-highlighted:text-foreground data-highlighted:bg-[var(--wash-hover)] focus:text-foreground focus:bg-[var(--wash-hover)]",
		"data-[state=checked]:text-primary data-[state=checked]:border-primary data-[state=checked]:bg-[var(--wash-active)]",
		"data-[state=indeterminate]:text-primary data-[state=indeterminate]:border-primary data-[state=indeterminate]:bg-[var(--wash-active)]",
		"data-[disabled]:pointer-events-none data-[disabled]:opacity-50 data-[inset]:pl-8",
		"[&_svg]:pointer-events-none [&_svg]:shrink-0",
		className
	)}
	{...restProps}
>
	{#snippet children({ checked, indeterminate })}
		<!-- Status markers are squares, never dots. -->
		<span
			class="pointer-events-none absolute right-4 flex items-center justify-center"
			data-slot="dropdown-menu-checkbox-item-indicator"
		>
			{#if indeterminate}
				<i class="bg-steel block h-[2px] w-[6px]"></i>
			{:else if checked}
				<i class="bg-primary block size-[6px]"></i>
			{/if}
		</span>
		{@render childrenProp?.()}
	{/snippet}
</DropdownMenuPrimitive.CheckboxItem>
