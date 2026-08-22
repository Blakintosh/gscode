<script lang="ts">
	import type { HTMLAttributes } from "svelte/elements";
	import { cn, type WithElementRef } from "$lib/utils.js";
	import Brush from "$lib/components/site/Brush.svelte";
	import type { ComponentProps } from "svelte";

	type BrushProps = ComponentProps<typeof Brush>;

	let {
		ref = $bindable(null),
		class: className,
		children,
		size = "default",
		rim = "rest",
		surface = "card",
		tab,
		tabClass,
		tabStyle,
		readout,
		handles = false,
		shadow = "none",
		bodyClass,
		...restProps
	}: WithElementRef<HTMLAttributes<HTMLElement>> & {
		size?: "default" | "sm";
		/** Passed through to Brush so pages can raise a card to active/danger. */
		rim?: BrushProps["rim"];
		surface?: BrushProps["surface"];
		tab?: string;
		tabClass?: string;
		tabStyle?: string;
		readout?: string;
		handles?: boolean;
		shadow?: BrushProps["shadow"];
		bodyClass?: string;
	} = $props();
</script>

<!-- A card is a Brush: 15px chamfer, 1px rim, panel body. -->
<Brush
	{rim}
	{surface}
	cut={15}
	{tab}
	{tabClass}
	{tabStyle}
	{readout}
	{handles}
	{shadow}
	data-slot="card"
	data-size={size}
	class={cn("text-card-foreground group/card text-sm", className)}
	bodyClass={cn(
		"flex flex-col has-data-[slot=card-footer]:pb-0",
		size === "sm" ? "gap-3 py-4" : "gap-4 py-6",
		bodyClass
	)}
	{...restProps}
>
	{@render children?.()}
</Brush>
