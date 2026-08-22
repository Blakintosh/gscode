<script lang="ts">
	import { DropdownMenu as DropdownMenuPrimitive } from "bits-ui";
	import { cn } from "$lib/utils.js";
	import type { Snippet } from "svelte";
	import type { WithoutChildrenOrChild } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		children,
		...restProps
	}: WithoutChildrenOrChild<DropdownMenuPrimitive.SubContentProps> & {
		children?: Snippet;
	} = $props();
</script>

<DropdownMenuPrimitive.SubContent
	bind:ref
	data-slot="dropdown-menu-sub-content"
	class={cn(
		"data-open:animate-in data-closed:animate-out data-closed:fade-out-0 data-open:fade-in-0 z-50 w-auto min-w-[196px] duration-150 outline-none [filter:drop-shadow(var(--shadow-overlay))]",
		className
	)}
	{...restProps}
>
	<div
		data-slot="dropdown-menu-sub-content-body"
		class="chamfer chamfer-sm rim-edge relative z-0 w-full py-1.5 font-mono text-xs before:absolute before:inset-px before:-z-10 before:bg-popover before:content-[''] before:[clip-path:inherit]"
	>
		{@render children?.()}
	</div>
</DropdownMenuPrimitive.SubContent>
