<script lang="ts">
	import { cn, type WithElementRef } from "$lib/utils.js";
	import type { HTMLLiAttributes } from "svelte/elements";

	let {
		ref = $bindable(null),
		class: className,
		children,
		...restProps
	}: WithElementRef<HTMLLiAttributes> = $props();
</script>

<!-- Datum separates path segments with a mono slash, not a chevron. -->
<li
	bind:this={ref}
	data-slot="breadcrumb-separator"
	role="presentation"
	aria-hidden="true"
	class={cn("text-steel select-none [&>svg]:size-3", className)}
	{...restProps}
>
	{#if children}
		{@render children?.()}
	{:else}
		/
	{/if}
</li>
