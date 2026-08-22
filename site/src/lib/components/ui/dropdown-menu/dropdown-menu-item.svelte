<script lang="ts">
	import { cn } from "$lib/utils.js";
	import { DropdownMenu as DropdownMenuPrimitive } from "bits-ui";

	let {
		ref = $bindable(null),
		class: className,
		inset,
		variant = "default",
		...restProps
	}: DropdownMenuPrimitive.ItemProps & {
		inset?: boolean;
		variant?: "default" | "destructive";
	} = $props();
</script>

<DropdownMenuPrimitive.Item
	bind:ref
	data-slot="dropdown-menu-item"
	data-inset={inset}
	data-variant={variant}
	class={cn(
		"text-muted-foreground group/dropdown-menu-item relative flex cursor-pointer items-center gap-2 border-l-2 border-transparent px-4 py-2 transition-colors outline-hidden select-none",
		"data-highlighted:text-foreground data-highlighted:bg-[var(--wash-hover)] focus:text-foreground focus:bg-[var(--wash-hover)]",
		"data-[variant=destructive]:text-destructive data-[variant=destructive]:data-highlighted:text-destructive data-[variant=destructive]:focus:text-destructive data-[variant=destructive]:data-highlighted:bg-[color-mix(in_oklab,var(--destructive)_10%,transparent)] data-[variant=destructive]:focus:bg-[color-mix(in_oklab,var(--destructive)_10%,transparent)] data-[variant=destructive]:*:[svg]:text-destructive",
		"data-disabled:pointer-events-none data-disabled:opacity-50 data-[inset]:pl-8",
		"[&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4 [&_svg:not([class*='text-'])]:text-muted-foreground",
		className
	)}
	{...restProps}
/>
