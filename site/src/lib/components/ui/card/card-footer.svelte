<script lang="ts">
	import { cn, type WithElementRef } from "$lib/utils.js";
	import type { HTMLAttributes } from "svelte/elements";

	let {
		ref = $bindable(null),
		class: className,
		children,
		...restProps
	}: WithElementRef<HTMLAttributes<HTMLDivElement>> = $props();
</script>

<div
	bind:this={ref}
	data-slot="card-footer"
	class={cn(
		"border-border text-dim flex items-center border-t px-6 pt-4 pb-6 font-mono text-[12px] tracking-[.04em] group-data-[size=sm]/card:px-4 group-data-[size=sm]/card:pb-4",
		className
	)}
	{...restProps}
>
	{@render children?.()}
</div>
