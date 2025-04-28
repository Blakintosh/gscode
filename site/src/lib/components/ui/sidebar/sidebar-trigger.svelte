<script lang="ts">
	import { Button } from "$lib/components/ui/button/index.js";
	import { cn } from "$lib/utils.js";
	// @ts-ignore
	import PanelLeft from "lucide-svelte/icons/panel-left";
	import type { ComponentProps, Snippet } from "svelte";
	import { useSidebar } from "./context.svelte.js";

	let {
		ref = $bindable(null),
		class: className,
		onclick,
		children,
		...restProps
	}: ComponentProps<typeof Button> & {
		onclick?: (e: MouseEvent) => void;
		children?: Snippet;
	} = $props();

	const sidebar = useSidebar();
</script>

<Button
	type="button"
	onclick={(e) => {
		onclick?.(e);
		sidebar.toggle();
	}}
	data-sidebar="trigger"
	variant="ghost"
	size="icon"
	class={cn("h-7 w-7", className)}
	{...restProps}
>
	{#if !children}
		<PanelLeft />
		<span class="sr-only">Toggle Sidebar</span>
	{:else}
		{@render children?.()}
	{/if}
</Button>
