<script lang="ts">
	import { Avatar as AvatarPrimitive } from "bits-ui";
	import { cn } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		loadingStatus = $bindable("loading"),
		size = "default",
		class: className,
		...restProps
	}: AvatarPrimitive.RootProps & {
		size?: "default" | "sm" | "lg";
	} = $props();
</script>

<!-- Datum: an avatar is a chamfered square, never a circle. The cut scales with the tile
     so a 24px avatar does not lose a third of itself to the corner. -->
<AvatarPrimitive.Root
	bind:ref
	bind:loadingStatus
	data-slot="avatar"
	data-size={size}
	class={cn(
		"group/avatar chamfer relative flex size-8 shrink-0 overflow-hidden select-none [--cut:8px] data-[size=lg]:size-10 data-[size=lg]:[--cut:10px] data-[size=sm]:size-6 data-[size=sm]:[--cut:6px]",
		className
	)}
	{...restProps}
/>
