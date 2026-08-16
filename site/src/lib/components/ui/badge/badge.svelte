<script lang="ts" module>
	import { type VariantProps, tv } from "tailwind-variants";

	/**
	 * Datum badge — two families, one type role (Cascadia Code, 2xs uppercase .14em).
	 *
	 * Chip   (`default` / `secondary` / `destructive` / `category`): a filled 7px
	 * badge-cut shape. Category chips take their colour from
	 *         `categoryChipStyle(cat)` inline vars via the `category-chip` utility.
	 * Status (`outline` / `live` / `pending` / `draft` / `rejected`): no clip, no fill,
	 * a 1px inset ring in the status colour. This is the shape the design system
	 * uses for Live / Pending review / Draft / Rejected.
	 *
	 * Version strings are not badges — they are `font-mono text-xs text-primary`.
	 */
	export const badgeVariants = tv({
		base: "group/badge inline-flex h-5 w-fit shrink-0 items-center justify-center gap-1.5 px-2.5 font-mono text-2xs leading-none tracking-label whitespace-nowrap uppercase transition-[filter,background,color] duration-150 outline-none focus-visible:[box-shadow:inset_0_0_0_2px_var(--ring)] [&>svg]:pointer-events-none [&>svg]:size-3",
		variants: {
			variant: {
				default: "badge-cut bg-primary text-primary-foreground [a]:hover:brightness-[1.08]",
				secondary: "badge-cut rimmed rimmed-popover text-muted-foreground [a]:hover:text-foreground",
				destructive: "badge-cut bg-destructive text-destructive-foreground [a]:hover:brightness-[1.08]",
				category: "badge-cut category-chip [a]:hover:brightness-[1.08]",
				outline: "text-primary shadow-[inset_0_0_0_1px_var(--deep)] [a]:hover:text-bright",
				live: "text-primary shadow-[inset_0_0_0_1px_var(--deep)] [a]:hover:text-bright",
				pending: "text-muted-foreground shadow-[inset_0_0_0_1px_var(--steel)] [a]:hover:text-foreground",
				draft: "text-dim shadow-[inset_0_0_0_1px_var(--border)] [a]:hover:text-muted-foreground",
				rejected:
					"text-destructive shadow-[inset_0_0_0_1px_color-mix(in_oklab,var(--destructive)_40%,transparent)] [a]:hover:brightness-[1.08]",
				ghost: "text-muted-foreground hover:bg-[var(--wash-hover)] hover:text-foreground",
				link: "text-primary underline-offset-4 hover:underline",
			},
		},
		defaultVariants: {
			variant: "default",
		},
	});

	export type BadgeVariant = VariantProps<typeof badgeVariants>["variant"];
</script>

<script lang="ts">
	import type { HTMLAnchorAttributes } from "svelte/elements";
	import { cn, type WithElementRef } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		href,
		class: className,
		variant = "default",
		children,
		...restProps
	}: WithElementRef<HTMLAnchorAttributes> & {
		variant?: BadgeVariant;
	} = $props();
</script>

<svelte:element
	this={href ? "a" : "span"}
	bind:this={ref}
	data-slot="badge"
	{href}
	class={cn(badgeVariants({ variant }), className)}
	{...restProps}
>
	{@render children?.()}
</svelte:element>
