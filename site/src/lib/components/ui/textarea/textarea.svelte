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

<!-- Datum: the same recess as Input (rim wrapper follows the chamfer), but Sora — mono
     never runs multi-line prose. Sizing classes land on the wrapper. -->
<span
	data-slot="textarea-rim"
	class={cn(
		"chamfer chamfer-sm rimmed rimmed-recess flex min-h-24 w-full text-foreground has-disabled:opacity-50",
		className
	)}
>
	<textarea
		bind:this={ref}
		data-slot="textarea"
		class="placeholder:text-dim block min-h-0 w-full min-w-0 flex-1 self-stretch border-0 bg-transparent p-4 font-sans text-sm leading-relaxed outline-none disabled:cursor-not-allowed"
		bind:value
		{...restProps}
	></textarea>
</span>
