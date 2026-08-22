<script lang="ts">
	import type { HTMLOlAttributes } from "svelte/elements";
	import { cn, type WithElementRef } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		children,
		...restProps
	}: WithElementRef<HTMLOlAttributes> = $props();
</script>

<!-- Datum: a breadcrumb is a readout — mono 10px uppercase in dim, separated by slashes. -->
<ol
	bind:this={ref}
	data-slot="breadcrumb-list"
	class={cn(
		"text-dim flex flex-wrap items-center gap-2 font-mono text-xs tracking-label uppercase wrap-break-word",
		className
	)}
	{...restProps}
>
	{@render children?.()}
</ol>
