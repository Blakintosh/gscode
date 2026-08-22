<script lang="ts">
	import { LinkPreview as HoverCardPrimitive } from "bits-ui";
	import { cn, type WithoutChildrenOrChild } from "$lib/utils.js";
	import HoverCardPortal from "./hover-card-portal.svelte";
	import type { ComponentProps } from "svelte";

	let {
		ref = $bindable(null),
		class: className,
		align = "center",
		sideOffset = 4,
		portalProps,
		children,
		...restProps
	}: HoverCardPrimitive.ContentProps & {
		portalProps?: WithoutChildrenOrChild<ComponentProps<typeof HoverCardPortal>>;
	} = $props();
</script>

<HoverCardPortal {...portalProps}>
	<HoverCardPrimitive.Content
		bind:ref
		data-slot="hover-card-content"
		{align}
		{sideOffset}
		class={cn(
			"data-open:animate-in data-open:fade-in-0 data-closed:animate-out data-closed:fade-out-0 z-50 w-64 origin-(--transform-origin) duration-150 outline-hidden [filter:drop-shadow(var(--shadow-overlay))]",
			className
		)}
		{...restProps}
	>
		<div
			data-slot="hover-card-content-body"
			class="chamfer chamfer-sm rim-edge text-popover-foreground relative z-0 p-4 text-sm before:absolute before:inset-px before:-z-10 before:bg-popover before:content-[''] before:[clip-path:inherit]"
		>
			{@render children?.()}
		</div>
	</HoverCardPrimitive.Content>
</HoverCardPortal>
