<script lang="ts" module>
	import { cn } from "$lib/utils.js";
	import { tv } from "tailwind-variants";

	// Header nav is mono, 10px, uppercase — teal when open or hovered.
	export const navigationMenuTriggerStyle = tv({
		base: "text-muted-foreground hover:text-primary focus-visible:text-primary data-open:text-primary data-popup-open:text-primary group/navigation-menu-trigger inline-flex h-auto w-max items-center justify-center gap-1 bg-transparent px-0 py-1 font-mono text-[12px] tracking-[.15em] uppercase transition-colors outline-none disabled:pointer-events-none disabled:opacity-50",
	});
</script>

<script lang="ts">
	import { NavigationMenu as NavigationMenuPrimitive } from "bits-ui";
	import ChevronDownIcon from "@lucide/svelte/icons/chevron-down";
	let {
		ref = $bindable(null),
		class: className,
		children,
		...restProps
	}: NavigationMenuPrimitive.TriggerProps = $props();
</script>

<NavigationMenuPrimitive.Trigger
	bind:ref
	data-slot="navigation-menu-trigger"
	class={cn(navigationMenuTriggerStyle(), "group", className)}
	{...restProps}
>
	{@render children?.()}
	<ChevronDownIcon
		class="relative top-px size-3 transition-transform duration-150 group-data-open/navigation-menu-trigger:rotate-180 group-data-popup-open/navigation-menu-trigger:rotate-180"
		aria-hidden="true"
	/>
</NavigationMenuPrimitive.Trigger>
