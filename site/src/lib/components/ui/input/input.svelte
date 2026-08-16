<script lang="ts">
	import type { HTMLInputAttributes, HTMLInputTypeAttribute } from "svelte/elements";
	import { cn, type WithElementRef } from "$lib/utils.js";

	type InputType = Exclude<HTMLInputTypeAttribute, "file">;

	type Props = WithElementRef<
		Omit<HTMLInputAttributes, "type"> &
			({ type: "file"; files?: FileList } | { type?: InputType; files?: undefined })
	>;

	let {
		ref = $bindable(null),
		value = $bindable(),
		type,
		files = $bindable(),
		class: className,
		"data-slot": dataSlot = "input",
		...restProps
	}: Props = $props();

	/* Datum: an input is a RECESS — one surface step below the panel it sits on, with a
	   1px edge drawn as an inset shadow (clip-path clips real borders). Focus swaps the
	   edge for the ring colour; there is no glow. Single-line fields are mono. */
	const base =
		"chamfer chamfer-sm bg-recess text-foreground placeholder:text-dim font-mono text-[13px] w-full min-w-0 border-0 px-4 h-11 outline-none transition-[box-shadow,color] shadow-[inset_0_0_0_1px_var(--border)] focus-visible:shadow-[inset_0_0_0_1px_var(--ring)] aria-invalid:shadow-[inset_0_0_0_1px_var(--destructive)] disabled:pointer-events-none disabled:cursor-not-allowed disabled:opacity-50";
	const fileBits =
		"file:font-display file:font-bold file:uppercase file:tracking-[.06em] file:text-[11px] file:bg-popover file:text-foreground file:border-0 file:h-8 file:px-3 file:mr-3 file:cursor-pointer file:inline-flex file:items-center";
</script>

{#if type === "file"}
	<input
		bind:this={ref}
		data-slot={dataSlot}
		class={cn(base, fileBits, className)}
		type="file"
		bind:files
		bind:value
		{...restProps}
	/>
{:else}
	<input
		bind:this={ref}
		data-slot={dataSlot}
		class={cn(base, className)}
		{type}
		bind:value
		{...restProps}
	/>
{/if}
