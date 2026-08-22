<script lang="ts">
	import { Checkbox as CheckboxPrimitive } from "bits-ui";
	import CheckIcon from '@lucide/svelte/icons/check';
	import { cn, type WithoutChildrenOrChild } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		checked = $bindable(false),
		indeterminate = $bindable(false),
		class: className,
		...restProps
	}: WithoutChildrenOrChild<CheckboxPrimitive.RootProps> = $props();
</script>

<!-- Datum: a 15px chamfered square. Unchecked = steel edge over the recess; checked =
 teal fill with an ink tick; indeterminate = a 7px ink square. Nothing is round. -->
<CheckboxPrimitive.Root
	bind:ref
	data-slot="checkbox"
	class={cn(
		"chamfer chamfer-2xs bg-recess text-ink peer size-[15px] shrink-0 cursor-pointer border-0 outline-none transition-colors",
		"shadow-[inset_0_0_0_1px_var(--steel)] focus-visible:shadow-[inset_0_0_0_1px_var(--ring)] aria-invalid:shadow-[inset_0_0_0_1px_var(--destructive)]",
		"data-[state=checked]:bg-primary data-[state=checked]:shadow-none data-[state=indeterminate]:bg-primary data-[state=indeterminate]:shadow-none",
		"disabled:cursor-not-allowed disabled:opacity-50",
		className
	)}
	bind:checked
	bind:indeterminate
	{...restProps}
>
	{#snippet children({ checked, indeterminate })}
		<span
			data-slot="checkbox-indicator"
			class="flex size-full items-center justify-center text-current"
		>
			{#if checked}
				<CheckIcon class="size-2.5 stroke-[3]" />
			{:else if indeterminate}
				<span class="block size-[7px] bg-current"></span>
			{/if}
		</span>
	{/snippet}
</CheckboxPrimitive.Root>
