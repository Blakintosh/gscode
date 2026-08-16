<script lang="ts">
	import type { HTMLAttributes } from "svelte/elements";
	import { cn, type WithElementRef } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		children,
		...restProps
	}: WithElementRef<HTMLAttributes<HTMLDivElement>> = $props();
</script>

<!-- Overlap stays; the separator is a 2px ground-coloured border (a ring would be eaten by
 the chamfer's clip-path). -->
<div
	bind:this={ref}
	data-slot="avatar-group"
	class={cn(
		"cn-avatar-group group/avatar-group flex -space-x-2 *:data-[slot=avatar]:border-2 *:data-[slot=avatar]:border-background",
		className
	)}
	{...restProps}
>
	{@render children?.()}
</div>
