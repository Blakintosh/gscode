<script lang="ts">
	import type { HTMLTextareaAttributes } from "svelte/elements";
	import { cn, type WithElementRef } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		value = $bindable(),
		class: className,
		...restProps
	}: WithElementRef<HTMLTextareaAttributes, HTMLTextAreaElement> = $props();
</script>

<!-- Datum: the same recess as Input, but Sora — mono never runs multi-line prose. -->
<textarea
	bind:this={ref}
	data-slot="textarea"
	class={cn(
		"chamfer chamfer-sm bg-recess text-foreground placeholder:text-dim block w-full min-h-24 border-0 p-4 font-sans text-sm leading-relaxed outline-none transition-[box-shadow,color] shadow-[inset_0_0_0_1px_var(--border)] focus-visible:shadow-[inset_0_0_0_1px_var(--ring)] aria-invalid:shadow-[inset_0_0_0_1px_var(--destructive)] disabled:cursor-not-allowed disabled:opacity-50",
		className
	)}
	bind:value
	{...restProps}
></textarea>
