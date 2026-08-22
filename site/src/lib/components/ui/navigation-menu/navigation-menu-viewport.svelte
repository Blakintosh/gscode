<script lang="ts">
	import { NavigationMenu as NavigationMenuPrimitive } from "bits-ui";
	import { cn } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		...restProps
	}: NavigationMenuPrimitive.ViewportProps = $props();
</script>

<!-- Shadow lives on the wrapper; the chamfered viewport below would clip it. -->
<div
	class={cn(
		"absolute start-0 top-full isolate z-50 flex justify-center [filter:drop-shadow(var(--shadow-overlay))]"
	)}
>
	<NavigationMenuPrimitive.Viewport
		bind:ref
		data-slot="navigation-menu-viewport"
		class={cn(
			"chamfer chamfer-sm rim-edge data-open:animate-in data-closed:animate-out data-open:fade-in-0 data-closed:fade-out-0 relative z-0 mt-2 h-[calc(var(--bits-navigation-menu-viewport-height)+1rem)] w-full origin-top-center overflow-hidden font-mono text-xs duration-150 before:absolute before:inset-px before:-z-10 before:bg-popover before:content-[''] before:[clip-path:inherit] md:w-[calc(var(--bits-navigation-menu-viewport-width)+1rem)]",
			className
		)}
		{...restProps}
	/>
</div>
