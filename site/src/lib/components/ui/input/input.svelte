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

	/* Datum: an input is a RECESS — one surface step below the panel it sits on, inside a
	   1px edge that follows the chamfer (the wrapper; inputs can't carry pseudo-elements).
	   Focus swaps the edge for the ring colour; there is no glow. Single-line fields are
	   mono. Sizing/colour classes passed in land on the wrapper; the control fills it. */
	const wrap =
		"chamfer chamfer-sm rimmed rimmed-recess flex h-11 w-full min-w-0 text-foreground has-disabled:opacity-50 has-disabled:pointer-events-none";
	const base =
		"placeholder:text-dim h-full min-h-0 w-full min-w-0 flex-1 border-0 bg-transparent px-4 font-mono text-[13px] outline-none disabled:cursor-not-allowed";
	const fileBits =
		"file:font-display file:font-bold file:uppercase file:tracking-[.06em] file:text-[11px] file:bg-popover file:text-foreground file:border-0 file:h-8 file:px-3 file:mr-3 file:cursor-pointer file:inline-flex file:items-center";
</script>

<span data-slot="input-rim" class={cn(wrap, className)}>
	{#if type === "file"}
		<input
			bind:this={ref}
			data-slot={dataSlot}
			class={cn(base, fileBits)}
			type="file"
			bind:files
			bind:value
			{...restProps}
		/>
	{:else}
		<input bind:this={ref} data-slot={dataSlot} class={base} {type} bind:value {...restProps} />
	{/if}
</span>
