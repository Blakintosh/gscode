<script lang="ts">
	import { Avatar as AvatarPrimitive } from "bits-ui";
	import { cn } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		...restProps
	}: AvatarPrimitive.FallbackProps = $props();
</script>

<!-- Fallback = the avatar gradient (142deg bright → teal → violet) with a Chakra initial in ink. -->
<AvatarPrimitive.Fallback
	bind:ref
	data-slot="avatar-fallback"
	class={cn(
		"grad-avatar text-ink font-display flex size-full items-center justify-center text-xs leading-none font-bold tracking-[.04em] uppercase group-data-[size=lg]/avatar:text-sm group-data-[size=sm]/avatar:text-[12px]",
		className
	)}
	{...restProps}
/>
