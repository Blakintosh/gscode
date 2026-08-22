<script lang="ts">
	import { NavigationMenu as NavigationMenuPrimitive } from "bits-ui";
	import { cn } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		...restProps
	}: NavigationMenuPrimitive.IndicatorProps = $props();
</script>

<!-- Datum has no floating arrow indicator: the panel's own notch-free rim is the cue. -->
<NavigationMenuPrimitive.Indicator
	bind:ref
	data-slot="navigation-menu-indicator"
	class={cn("hidden", className)}
	{...restProps}
>
	<div class="bg-border relative top-[60%] size-2 rotate-45"></div>
</NavigationMenuPrimitive.Indicator>
