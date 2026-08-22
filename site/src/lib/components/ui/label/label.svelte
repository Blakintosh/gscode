<script lang="ts">
	import { Label as LabelPrimitive } from "bits-ui";
	import { cn } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		...restProps
	}: LabelPrimitive.RootProps = $props();
</script>

<!-- Datum: labels are mono, never Sora. 10px uppercase .17em in mute, sat 8px above its field. -->
<LabelPrimitive.Root
	bind:ref
	data-slot="label"
	class={cn(
		"type-label text-muted-foreground mb-2 flex select-none items-center gap-1.5 peer-disabled:cursor-not-allowed peer-disabled:opacity-50 [&_svg]:size-3.5 [&_svg]:shrink-0",
		className
	)}
	{...restProps}
/>
