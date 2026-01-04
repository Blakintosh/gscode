<script lang="ts">
	import { type ScrDataType, ScrEntityTypes } from '$lib/models/library';
	import * as Select from '$lib/components/ui/select/index.js';

	interface Props {
		value: ScrDataType | null | undefined;
		onchange: (type: ScrDataType | null) => void;
	}

	let { value, onchange }: Props = $props();

	// Entity type options for dropdown
	const entityTypeOptions = [
		{ value: '', label: 'any' },
		...ScrEntityTypes.map((t) => ({ value: t, label: t }))
	];

	// Derived state - prioritize subType over instanceType
	let subType = $derived(value?.subType ?? value?.instanceType ?? '');

	function handleSubTypeChange(newSubType: string | undefined) {
		onchange({
			dataType: 'entity',
			instanceType: newSubType || null,
			subType: newSubType || null,
			isArray: false
		});
	}
</script>

<div class="flex items-center gap-2">
	<div class="flex flex-col gap-1">
		<span class="text-xs text-muted-foreground">Entity Type</span>
		<Select.Root type="single" value={subType} onValueChange={handleSubTypeChange}>
			<Select.Trigger class="w-40">
				{#if subType}
					<span>{subType}</span>
				{:else}
					<span class="text-muted-foreground">any</span>
				{/if}
			</Select.Trigger>
			<Select.Content>
				{#each entityTypeOptions as option (option.value)}
					<Select.Item value={option.value}>{option.label}</Select.Item>
				{/each}
			</Select.Content>
		</Select.Root>
	</div>
</div>
