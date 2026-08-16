<script lang="ts">
	import { Dialog as DialogPrimitive } from "bits-ui";
	import XIcon from "@lucide/svelte/icons/x";
	import type { Snippet } from "svelte";
	import type { ComponentProps } from "svelte";
	import DialogOverlay from "./dialog-overlay.svelte";
	import DialogPortal from "./dialog-portal.svelte";
	import { cn, type WithoutChildrenOrChild } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		portalProps,
		showCloseButton = true,
		variant = "default",
		children,
		...restProps
	}: WithoutChildrenOrChild<DialogPrimitive.ContentProps> & {
		portalProps?: WithoutChildrenOrChild<ComponentProps<typeof DialogPortal>>;
		showCloseButton?: boolean;
		/** `destructive` swaps the rim for the coral-topped gradient. */
		variant?: "default" | "destructive";
		children: Snippet;
	} = $props();
</script>

<DialogPortal {...portalProps}>
	<DialogOverlay />
	<!-- Wrapper: centring + the drop shadow (clip-path would eat a box shadow). -->
	<DialogPrimitive.Content
		bind:ref
		data-slot="dialog-content"
		data-variant={variant}
		class={cn(
			"data-open:animate-in data-closed:animate-out data-closed:fade-out-0 data-open:fade-in-0 fixed top-1/2 left-1/2 z-50 w-full max-w-md -translate-x-1/2 -translate-y-1/2 duration-150 [filter:drop-shadow(var(--shadow-overlay))]",
			className
		)}
		{...restProps}
	>
		<!-- Rim: the chamfered brush; ::before is the raise body, inset 1px. -->
		<div
			data-slot="dialog-content-body"
			class={cn(
				"chamfer relative z-0 flex flex-col gap-4 p-6 pb-5",
				"before:absolute before:inset-px before:-z-10 before:content-[''] before:[clip-path:inherit] before:bg-popover",
				variant === "destructive" ? "rim-danger" : "rim"
			)}
		>
			{@render children?.()}
			{#if showCloseButton}
				<DialogPrimitive.Close
					class="chamfer chamfer-xs text-muted-foreground hover:text-foreground hover:bg-[var(--wash-hover)] focus-visible:text-foreground focus-visible:bg-[var(--wash-active)] absolute top-3 right-3 flex size-8 items-center justify-center transition-colors outline-none disabled:pointer-events-none"
				>
					<XIcon class="size-4" />
					<span class="sr-only">Close</span>
				</DialogPrimitive.Close>
			{/if}
		</div>
	</DialogPrimitive.Content>
</DialogPortal>
