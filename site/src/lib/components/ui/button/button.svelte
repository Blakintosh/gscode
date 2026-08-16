<script lang="ts" module>
	import { cn, type WithElementRef } from "$lib/utils.js";
	import type { HTMLAnchorAttributes, HTMLButtonAttributes } from "svelte/elements";
	import { type VariantProps, tv } from "tailwind-variants";

	/**
	 * Datum button. Chakra Petch 700, uppercase, chamfered (10px at default size),
	 * 150ms colour/filter transitions and a 1px press. Nothing is round.
	 *
	 * clip-path clips borders, outlines and box-shadows, so the rimmed variants
	 * (secondary / outline) paint the rim as the element background and inset a
	 * `::before` body 1px inside it, inheriting the same clip-path. Focus swaps the
	 * rim colour to --ring rather than drawing an outer ring.
	 *
	 * `size` is declared before `variant` on purpose: tailwind-variants emits classes in
	 * key order, so `variant` wins the merge (the `link` variant can drop the size box).
	 */
	export const buttonVariants = tv({
		base: "group/button relative inline-flex shrink-0 cursor-pointer items-center justify-center gap-1.5 font-display font-bold uppercase leading-none whitespace-nowrap outline-none select-none transition-[filter,background,color] duration-150 active:translate-y-px disabled:pointer-events-none disabled:cursor-default disabled:opacity-35 aria-disabled:pointer-events-none aria-disabled:opacity-35 [&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4",
		variants: {
			size: {
				default: "h-11 px-6 text-[13px] tracking-[.06em] [--cut:10px]",
				xs: "h-7 gap-1 px-3 text-[10px] tracking-[.07em] [--cut:6px] [&_svg:not([class*='size-'])]:size-3",
				sm: "h-9 gap-1.5 px-4 text-[11px] tracking-[.07em] [--cut:8px] [&_svg:not([class*='size-'])]:size-3.5",
				lg: "h-13 gap-2 px-[30px] text-sm tracking-[.06em] [--cut:12px]",
				icon: "size-9 p-0 [--cut:8px]",
				"icon-xs": "size-7 p-0 [--cut:6px] [&_svg:not([class*='size-'])]:size-3.5",
				"icon-sm": "size-8 p-0 [--cut:7px] [&_svg:not([class*='size-'])]:size-3.5",
				"icon-lg": "size-11 p-0 [--cut:10px]",
			},
			variant: {
				default:
					"chamfer grad-action hover:brightness-[1.08] focus-visible:[box-shadow:inset_0_0_0_2px_var(--ink)]",
				secondary:
					"chamfer rim-deep text-primary z-0 before:absolute before:inset-px before:-z-10 before:content-[''] before:[clip-path:inherit] before:bg-popover hover:text-bright hover:before:bg-deep aria-expanded:text-bright aria-expanded:before:bg-deep focus-visible:[background:var(--ring)]",
				outline:
					"chamfer rim-edge text-foreground z-0 before:absolute before:inset-px before:-z-10 before:content-[''] before:[clip-path:inherit] before:bg-card hover:[background:var(--primary)] hover:text-primary dark:hover:text-bright aria-expanded:[background:var(--primary)] aria-expanded:text-primary dark:aria-expanded:text-bright focus-visible:[background:var(--ring)]",
				ghost:
					"text-muted-foreground hover:bg-[var(--wash-hover)] hover:text-foreground dark:hover:text-bright aria-expanded:bg-[var(--wash-hover)] aria-expanded:text-foreground focus-visible:bg-[var(--wash-hover)] focus-visible:[box-shadow:inset_0_0_0_1px_var(--ring)]",
				destructive:
					"chamfer bg-destructive text-destructive-foreground hover:brightness-[1.08] focus-visible:[box-shadow:inset_0_0_0_2px_var(--destructive-foreground)]",
				link: "h-auto p-0 font-sans font-medium normal-case tracking-normal text-primary underline underline-offset-4 hover:text-bright focus-visible:text-bright",
			},
		},
		defaultVariants: {
			variant: "default",
			size: "default",
		},
	});

	export type ButtonVariant = VariantProps<typeof buttonVariants>["variant"];
	export type ButtonSize = VariantProps<typeof buttonVariants>["size"];

	export type ButtonProps = WithElementRef<HTMLButtonAttributes> &
		WithElementRef<HTMLAnchorAttributes> & {
			variant?: ButtonVariant;
			size?: ButtonSize;
		};
</script>

<script lang="ts">
	let {
		class: className,
		variant = "default",
		size = "default",
		ref = $bindable(null),
		href = undefined,
		type = "button",
		disabled,
		children,
		...restProps
	}: ButtonProps = $props();
</script>

{#if href}
	<a
		bind:this={ref}
		data-slot="button"
		class={cn(buttonVariants({ variant, size }), className)}
		href={disabled ? undefined : href}
		aria-disabled={disabled}
		role={disabled ? "link" : undefined}
		tabindex={disabled ? -1 : undefined}
		{...restProps}
	>
		{@render children?.()}
	</a>
{:else}
	<button
		bind:this={ref}
		data-slot="button"
		class={cn(buttonVariants({ variant, size }), className)}
		{type}
		{disabled}
		{...restProps}
	>
		{@render children?.()}
	</button>
{/if}
