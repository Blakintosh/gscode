<script lang="ts" module>
	import { tv, type VariantProps } from "tailwind-variants";

	/* Datum: tabs are the one un-chamfered component — a mono uppercase rail on a single
	   bottom edge. Both variants render the same rail; `line` is kept for API parity. */
	export const tabsListVariants = tv({
		base: "group/tabs-list border-border text-muted-foreground flex h-auto w-full items-center justify-start gap-0.5 overflow-x-auto border-b bg-transparent p-0 [scrollbar-width:none] font-mono text-[11px] tracking-[.12em] uppercase group-data-[orientation=vertical]/tabs:h-fit group-data-[orientation=vertical]/tabs:w-fit group-data-[orientation=vertical]/tabs:flex-col group-data-[orientation=vertical]/tabs:items-stretch group-data-[orientation=vertical]/tabs:border-r group-data-[orientation=vertical]/tabs:border-b-0",
		variants: {
			variant: {
				default: "cn-tabs-list-variant-default",
				line: "cn-tabs-list-variant-line",
			},
		},
		defaultVariants: {
			variant: "default",
		},
	});

	export type TabsListVariant = VariantProps<typeof tabsListVariants>["variant"];
</script>

<script lang="ts">
	import { Tabs as TabsPrimitive } from "bits-ui";
	import { cn } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		variant = "default",
		class: className,
		...restProps
	}: TabsPrimitive.ListProps & {
		variant?: TabsListVariant;
	} = $props();
</script>

<TabsPrimitive.List
	bind:ref
	data-slot="tabs-list"
	data-variant={variant}
	class={cn(tabsListVariants({ variant }), className)}
	{...restProps}
/>
