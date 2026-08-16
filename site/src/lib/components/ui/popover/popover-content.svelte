<script lang="ts">
	import { Popover as PopoverPrimitive } from "bits-ui";
	import { cn, type WithoutChildrenOrChild } from "$lib/utils.js";
	import PopoverPortal from "./popover-portal.svelte";
	import type { ComponentProps } from "svelte";

	let {
		ref = $bindable(null),
		class: className,
		sideOffset = 4,
		align = "center",
		portalProps,
		children,
		...restProps
	}: PopoverPrimitive.ContentProps & {
		portalProps?: WithoutChildrenOrChild<ComponentProps<typeof PopoverPortal>>;
	} = $props();
</script>

<PopoverPortal {...portalProps}>
	<PopoverPrimitive.Content
		bind:ref
		data-slot="popover-content"
		{sideOffset}
		{align}
		class={cn(
			"data-open:animate-in data-open:fade-in-0 data-closed:animate-out data-closed:fade-out-0 z-50 w-72 origin-(--transform-origin) duration-150 outline-hidden [filter:drop-shadow(var(--shadow-overlay))]",
			className
		)}
		{...restProps}
	>
		<!-- Overlays float on the raise colour with a real shadow; the wrapper carries it. -->
		<div
			data-slot="popover-content-body"
			class="chamfer chamfer-sm rim-edge text-popover-foreground relative z-0 flex flex-col gap-2.5 p-4 text-sm before:absolute before:inset-px before:-z-10 before:bg-popover before:content-[''] before:[clip-path:inherit]"
		>
			{@render children?.()}
		</div>
	</PopoverPrimitive.Content>
</PopoverPortal>
