<script lang="ts">
	import type { HTMLAttributes } from "svelte/elements";
	import { cn, type WithElementRef } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		children,
		...restProps
	}: WithElementRef<HTMLAttributes<HTMLSpanElement>> = $props();
</script>

<!-- Status markers in Datum are squares, never dots. The 2px knockout is a box-shadow in
     the ground colour rather than a ring so it stays radius-free. -->
<span
	bind:this={ref}
	data-slot="avatar-badge"
	class={cn(
		"bg-primary text-primary-foreground absolute right-0 bottom-0 z-10 inline-flex items-center justify-center [box-shadow:0_0_0_2px_var(--background)] select-none",
		"group-data-[size=sm]/avatar:size-2 group-data-[size=sm]/avatar:[&>svg]:hidden",
		"group-data-[size=default]/avatar:size-2.5 group-data-[size=default]/avatar:[&>svg]:size-2",
		"group-data-[size=lg]/avatar:size-3 group-data-[size=lg]/avatar:[&>svg]:size-2",
		className
	)}
	{...restProps}
>
	{@render children?.()}
</span>
