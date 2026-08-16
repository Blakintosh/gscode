<script lang="ts">
	import { Tabs as TabsPrimitive } from "bits-ui";
	import { cn } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		...restProps
	}: TabsPrimitive.TriggerProps = $props();
</script>

<TabsPrimitive.Trigger
	bind:ref
	data-slot="tabs-trigger"
	class={cn(
		"text-muted-foreground hover:text-foreground relative -mb-px inline-flex cursor-pointer items-center justify-center gap-1.5 border-0 border-b-2 border-transparent bg-transparent px-4 py-2.5 whitespace-nowrap transition-colors outline-none",
		"data-[state=active]:text-primary data-[state=active]:border-primary data-[state=active]:bg-transparent data-[state=active]:shadow-none",
		"focus-visible:outline-ring focus-visible:outline-1 focus-visible:-outline-offset-1",
		"disabled:pointer-events-none disabled:opacity-50 [&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4",
		"group-data-[orientation=vertical]/tabs:mb-0 group-data-[orientation=vertical]/tabs:-mr-px group-data-[orientation=vertical]/tabs:w-full group-data-[orientation=vertical]/tabs:justify-start group-data-[orientation=vertical]/tabs:border-r-2 group-data-[orientation=vertical]/tabs:border-b-0",
		className
	)}
	{...restProps}
/>
