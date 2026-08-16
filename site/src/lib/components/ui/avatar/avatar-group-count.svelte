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

<div
	bind:this={ref}
	data-slot="avatar-group-count"
	class={cn(
		"bg-muted text-muted-foreground border-background chamfer relative flex size-8 shrink-0 items-center justify-center border-2 font-mono text-[10px] leading-none tracking-[.06em] [--cut:8px] group-has-data-[size=lg]/avatar-group:size-10 group-has-data-[size=lg]/avatar-group:[--cut:10px] group-has-data-[size=sm]/avatar-group:size-6 group-has-data-[size=sm]/avatar-group:[--cut:6px] [&>svg]:size-4 group-has-data-[size=lg]/avatar-group:[&>svg]:size-5 group-has-data-[size=sm]/avatar-group:[&>svg]:size-3",
		className
	)}
	{...restProps}
>
	{@render children?.()}
</div>
