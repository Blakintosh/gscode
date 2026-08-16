<script lang="ts">
	import { cn, type WithElementRef, type WithoutChildren } from "$lib/utils.js";
	import type { HTMLAttributes } from "svelte/elements";

	let {
		ref = $bindable(null),
		class: className,
		...restProps
	}: WithoutChildren<WithElementRef<HTMLAttributes<HTMLDivElement>>> = $props();
</script>

<!-- Datum: the sheen travels top-left → bottom-right, along the single light source.
     Blocks are chamfered at control size; text lines can drop the chamfer with `[--cut:0px]`. -->
<div
	bind:this={ref}
	data-slot="skeleton"
	class={cn("sheen chamfer chamfer-sm", className)}
	{...restProps}
></div>
