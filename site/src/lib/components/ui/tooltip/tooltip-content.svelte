<script lang="ts">
	import { Tooltip as TooltipPrimitive } from "bits-ui";
	import { cn } from "$lib/utils.js";
	import TooltipPortal from "./tooltip-portal.svelte";
	import type { ComponentProps } from "svelte";
	import type { WithoutChildrenOrChild } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		sideOffset = 0,
		side = "top",
		children,
		arrowClasses,
		portalProps,
		...restProps
	}: TooltipPrimitive.ContentProps & {
		arrowClasses?: string;
		portalProps?: WithoutChildrenOrChild<ComponentProps<typeof TooltipPortal>>;
	} = $props();
</script>

<TooltipPortal {...portalProps}>
	<!-- Outer element stays unclipped so the notch can hang outside the brush. -->
	<TooltipPrimitive.Content
		bind:ref
		data-slot="tooltip-content"
		{sideOffset}
		{side}
		class={cn(
			"data-open:animate-in data-open:fade-in-0 data-[state=delayed-open]:animate-in data-[state=delayed-open]:fade-in-0 data-closed:animate-out data-closed:fade-out-0 z-50 w-fit duration-150",
			className
		)}
		{...restProps}
	>
		<div
			data-slot="tooltip-content-body"
			class="chamfer chamfer-xs rim-edge text-foreground relative z-0 px-3 py-[7px] font-mono text-2xs whitespace-nowrap before:absolute before:inset-px before:-z-10 before:bg-popover before:content-[''] before:[clip-path:inherit]"
		>
			{@render children?.()}
		</div>
		<TooltipPrimitive.Arrow>
			{#snippet child({ props })}
				<!-- The notch is a 9px square on the raise surface with a 1px inset edge. -->
				<div
					class={cn(
						"bg-popover z-50 size-[9px] rotate-45 shadow-[inset_0_0_0_1px_var(--border)]",
						"data-[side=top]:translate-x-1/2 data-[side=top]:translate-y-[calc(-50%+2px)]",
						"data-[side=bottom]:-translate-x-1/2 data-[side=bottom]:-translate-y-[calc(-50%+1px)]",
						"data-[side=right]:translate-x-[calc(50%+2px)] data-[side=right]:translate-y-1/2",
						"data-[side=left]:-translate-y-[calc(50%-3px)]",
						arrowClasses
					)}
					{...props}
				></div>
			{/snippet}
		</TooltipPrimitive.Arrow>
	</TooltipPrimitive.Content>
</TooltipPortal>
