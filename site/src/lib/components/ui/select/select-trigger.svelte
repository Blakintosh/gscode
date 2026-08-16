<script lang="ts">
	import { Select as SelectPrimitive } from "bits-ui";
	import { cn, type WithoutChild } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		children,
		size = "default",
		...restProps
	}: WithoutChild<SelectPrimitive.TriggerProps> & {
		size?: "sm" | "default";
	} = $props();
</script>

<!-- Datum: the trigger is a recess, like an input, with a teal caret in the corner. -->
<SelectPrimitive.Trigger
	bind:ref
	data-slot="select-trigger"
	data-size={size}
	class={cn(
		"chamfer chamfer-sm rimmed rimmed-recess text-foreground data-placeholder:text-dim flex w-fit cursor-pointer items-center justify-between gap-3 border-0 px-4 font-mono text-sm whitespace-nowrap transition-[box-shadow,color] outline-none select-none",
		"data-[size=default]:h-11 data-[size=sm]:h-9 data-[size=sm]:text-xs",
		"*:data-[slot=select-value]:line-clamp-1 *:data-[slot=select-value]:flex *:data-[slot=select-value]:items-center *:data-[slot=select-value]:gap-1.5",
		"disabled:cursor-not-allowed disabled:opacity-50 [&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4",
		className
	)}
	{...restProps}
>
	{@render children?.()}
	<span aria-hidden="true" class="text-primary pointer-events-none leading-none">&#9662;</span>
</SelectPrimitive.Trigger>
