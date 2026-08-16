<script lang="ts">
	import { NavigationMenu as NavigationMenuPrimitive } from "bits-ui";
	import { cn } from "$lib/utils.js";
	import type { Snippet } from "svelte";
	import type { WithoutChildrenOrChild } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		children,
		...restProps
	}: WithoutChildrenOrChild<NavigationMenuPrimitive.ContentProps> & {
		children?: Snippet;
	} = $props();
</script>

<!--
	When the root runs without a viewport this element is the floating panel, so it
	carries the drop shadow (a clipped element cannot) and the body below carries
	the chamfer + rim. Links inside become sidebar rows: 2px left border + wash.
-->
<NavigationMenuPrimitive.Content
	bind:ref
	data-slot="navigation-menu-content"
	class={cn(
		"data-[motion^=from-]:animate-in data-[motion^=to-]:animate-out data-[motion^=from-]:fade-in data-[motion^=to-]:fade-out top-0 left-0 w-full duration-150 md:absolute md:w-auto",
		"group-data-[viewport=false]/navigation-menu:data-open:animate-in group-data-[viewport=false]/navigation-menu:data-open:fade-in-0 group-data-[viewport=false]/navigation-menu:data-closed:animate-out group-data-[viewport=false]/navigation-menu:data-closed:fade-out-0",
		"group-data-[viewport=false]/navigation-menu:top-full group-data-[viewport=false]/navigation-menu:mt-2 group-data-[viewport=false]/navigation-menu:[filter:drop-shadow(var(--shadow-overlay))]",
		"**:data-[slot=navigation-menu-link]:focus:outline-none",
		// Menu rows: mono 12px, full width, 2px left border, teal wash when active.
		"[&_[data-slot=navigation-menu-link]]:w-full [&_[data-slot=navigation-menu-link]]:gap-2 [&_[data-slot=navigation-menu-link]]:border-l-2 [&_[data-slot=navigation-menu-link]]:border-transparent [&_[data-slot=navigation-menu-link]]:px-4 [&_[data-slot=navigation-menu-link]]:py-2 [&_[data-slot=navigation-menu-link]]:text-xs [&_[data-slot=navigation-menu-link]]:tracking-normal [&_[data-slot=navigation-menu-link]]:normal-case",
		"[&_[data-slot=navigation-menu-link]:hover]:text-foreground [&_[data-slot=navigation-menu-link]:hover]:bg-[var(--wash-hover)] [&_[data-slot=navigation-menu-link]:focus-visible]:text-foreground [&_[data-slot=navigation-menu-link]:focus-visible]:bg-[var(--wash-hover)]",
		"[&_[data-slot=navigation-menu-link][data-active]]:text-primary [&_[data-slot=navigation-menu-link][data-active]]:border-primary [&_[data-slot=navigation-menu-link][data-active]]:bg-[var(--wash-active)]",
		className
	)}
	{...restProps}
>
	<div
		data-slot="navigation-menu-content-body"
		class="group-data-[viewport=false]/navigation-menu:chamfer group-data-[viewport=false]/navigation-menu:chamfer-sm group-data-[viewport=false]/navigation-menu:rim-edge group-data-[viewport=false]/navigation-menu:relative group-data-[viewport=false]/navigation-menu:z-0 group-data-[viewport=false]/navigation-menu:before:absolute group-data-[viewport=false]/navigation-menu:before:inset-px group-data-[viewport=false]/navigation-menu:before:-z-10 group-data-[viewport=false]/navigation-menu:before:bg-popover group-data-[viewport=false]/navigation-menu:before:content-[''] group-data-[viewport=false]/navigation-menu:before:[clip-path:inherit] p-1.5 font-mono text-xs"
	>
		{@render children?.()}
	</div>
</NavigationMenuPrimitive.Content>
