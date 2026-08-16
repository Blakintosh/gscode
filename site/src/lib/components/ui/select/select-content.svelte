<script lang="ts">
	import { Select as SelectPrimitive } from "bits-ui";
	import SelectPortal from "./select-portal.svelte";
	import SelectScrollUpButton from "./select-scroll-up-button.svelte";
	import SelectScrollDownButton from "./select-scroll-down-button.svelte";
	import { cn, type WithoutChild } from "$lib/utils.js";
	import type { ComponentProps } from "svelte";
	import type { WithoutChildrenOrChild } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		sideOffset = 4,
		portalProps,
		children,
		preventScroll = true,
		...restProps
	}: WithoutChild<SelectPrimitive.ContentProps> & {
		portalProps?: WithoutChildrenOrChild<ComponentProps<typeof SelectPortal>>;
	} = $props();
</script>

<!-- Datum: an overlay floats on raise behind an edge rim, with a real drop shadow.
     clip-path kills box-shadow, so the shadow is a filter and the rim is the background. -->
<SelectPortal {...portalProps}>
	<SelectPrimitive.Content
		bind:ref
		{sideOffset}
		{preventScroll}
		data-slot="select-content"
		class={cn(
			"chamfer chamfer-sm rim-edge text-popover-foreground relative isolate z-50 min-w-36 overflow-x-hidden overflow-y-auto py-1.5 font-mono text-xs [filter:drop-shadow(var(--shadow-overlay))]",
			"before:bg-popover before:absolute before:inset-px before:-z-10 before:[clip-path:inherit] before:content-['']",
			"data-open:animate-in data-open:fade-in-0 data-closed:animate-out data-closed:fade-out-0 duration-150",
			className
		)}
		{...restProps}
	>
		<SelectScrollUpButton />
		<SelectPrimitive.Viewport
			class={cn(
				"h-(--bits-select-anchor-height) w-full min-w-(--bits-select-anchor-width) scroll-my-1"
			)}
		>
			{@render children?.()}
		</SelectPrimitive.Viewport>
		<SelectScrollDownButton />
	</SelectPrimitive.Content>
</SelectPortal>
