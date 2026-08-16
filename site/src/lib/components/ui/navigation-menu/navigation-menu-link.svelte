<script lang="ts">
	import { NavigationMenu as NavigationMenuPrimitive } from "bits-ui";
	import { cn } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		...restProps
	}: NavigationMenuPrimitive.LinkProps = $props();
</script>

<!--
	Two jobs, one class set: on the bar it reads like a trigger (mono 10px uppercase),
	inside a menu panel `navigation-menu-content` overrides it into a sidebar row.
-->
<NavigationMenuPrimitive.Link
	bind:ref
	data-slot="navigation-menu-link"
	class={cn(
		"text-muted-foreground hover:text-primary focus-visible:text-primary data-active:text-primary flex w-max items-center gap-1.5 bg-transparent px-0 py-1 font-mono text-[12px] tracking-[.15em] uppercase transition-colors outline-none",
		"[&_svg:not([class*='size-'])]:size-4",
		className
	)}
	{...restProps}
/>
