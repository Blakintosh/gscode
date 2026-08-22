<script lang="ts">
	import { Command as CommandPrimitive } from "bits-ui";
	import { cn } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		children,
		...restProps
	}: CommandPrimitive.ItemProps = $props();
</script>

<CommandPrimitive.Item
	bind:ref
	data-slot="command-item"
	class={cn(
		"group/command-item text-muted-foreground relative flex cursor-pointer items-center gap-2 border-l-2 border-transparent py-2 pr-9 pl-4 font-mono text-xs outline-hidden select-none",
		"data-selected:bg-[var(--wash-hover)] data-selected:text-foreground",
		"data-[checked=true]:border-primary data-[checked=true]:bg-[var(--wash-active)] data-[checked=true]:text-primary",
		"data-[disabled=true]:pointer-events-none data-[disabled=true]:opacity-50 [&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4",
		className
	)}
	{...restProps}
>
	{@render children?.()}
	<!-- Datum: a checked row is marked by the teal square, matching the select. -->
	<span
		aria-hidden="true"
		class="bg-primary absolute end-4 top-1/2 size-1.5 -translate-y-1/2 opacity-0 group-data-[checked=true]/command-item:opacity-100"
	></span>
</CommandPrimitive.Item>
