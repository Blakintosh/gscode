<script lang="ts">
	import { cn, type WithoutChildrenOrChild } from "$lib/utils.js";
	import DropdownMenuPortal from "./dropdown-menu-portal.svelte";
	import { DropdownMenu as DropdownMenuPrimitive } from "bits-ui";
	import type { ComponentProps, Snippet } from "svelte";

	let {
		ref = $bindable(null),
		sideOffset = 6,
		align = "start",
		portalProps,
		class: className,
		children,
		...restProps
	}: WithoutChildrenOrChild<DropdownMenuPrimitive.ContentProps> & {
		portalProps?: WithoutChildrenOrChild<ComponentProps<typeof DropdownMenuPortal>>;
		children?: Snippet;
	} = $props();
</script>

<DropdownMenuPortal {...portalProps}>
	<!-- Wrapper carries the drop shadow; the clipped rim inside would swallow it. -->
	<DropdownMenuPrimitive.Content
		bind:ref
		data-slot="dropdown-menu-content"
		{sideOffset}
		{align}
		class={cn(
			"data-open:animate-in data-closed:animate-out data-closed:fade-out-0 data-open:fade-in-0 z-50 min-w-[196px] w-(--bits-dropdown-menu-anchor-width) duration-150 outline-none [filter:drop-shadow(var(--shadow-overlay))]",
			className
		)}
		{...restProps}
	>
		<div
			data-slot="dropdown-menu-content-body"
			class="chamfer chamfer-sm rim-edge relative z-0 max-h-[inherit] w-full overflow-x-hidden overflow-y-auto py-1.5 font-mono text-xs before:absolute before:inset-px before:-z-10 before:bg-popover before:content-[''] before:[clip-path:inherit]"
		>
			{@render children?.()}
		</div>
	</DropdownMenuPrimitive.Content>
</DropdownMenuPortal>
