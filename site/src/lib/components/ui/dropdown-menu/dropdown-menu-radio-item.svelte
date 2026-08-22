<script lang="ts">
	import { DropdownMenu as DropdownMenuPrimitive } from "bits-ui";
	import { cn, type WithoutChild } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		children: childrenProp,
		...restProps
	}: WithoutChild<DropdownMenuPrimitive.RadioItemProps> = $props();
</script>

<DropdownMenuPrimitive.RadioItem
	bind:ref
	data-slot="dropdown-menu-radio-item"
	class={cn(
		"text-muted-foreground relative flex cursor-pointer items-center gap-2 border-l-2 border-transparent py-2 pr-9 pl-4 transition-colors outline-hidden select-none",
		"data-highlighted:text-foreground data-highlighted:bg-[var(--wash-hover)] focus:text-foreground focus:bg-[var(--wash-hover)]",
		"data-[state=checked]:text-primary data-[state=checked]:border-primary data-[state=checked]:bg-[var(--wash-active)]",
		"data-[disabled]:pointer-events-none data-[disabled]:opacity-50",
		"[&_svg]:pointer-events-none [&_svg]:shrink-0",
		className
	)}
	{...restProps}
>
	{#snippet children({ checked })}
		<span
			class="pointer-events-none absolute right-4 flex items-center justify-center"
			data-slot="dropdown-menu-radio-item-indicator"
		>
			{#if checked}
				<i class="bg-primary block size-[6px]"></i>
			{/if}
		</span>
		{@render childrenProp?.({ checked })}
	{/snippet}
</DropdownMenuPrimitive.RadioItem>
