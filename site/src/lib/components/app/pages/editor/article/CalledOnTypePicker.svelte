<script lang="ts">
	import type { ScrDataType } from '$lib/models/library';
	import * as Select from '$lib/components/ui/select/index.js';
	import { Input } from '$lib/components/ui/input/index.js';

	interface Props {
		value: ScrDataType | null | undefined;
		onchange: (type: ScrDataType | null) => void;
	}

	let { value, onchange }: Props = $props();

	// Only struct and entity are valid for calledOn
	const allowedTypes = [
		{ value: 'struct', label: 'struct' },
		{ value: 'entity', label: 'entity' }
	];

	// Derived state from value
	let selectedType = $derived(value?.dataType ?? '');
	let instanceType = $derived(value?.instanceType ?? '');

	function handleTypeChange(newType: string | undefined) {
		if (!newType) {
			onchange(null);
			return;
		}

		onchange({
			dataType: newType,
			instanceType: instanceType || null,
			isArray: false // Arrays are never valid for calledOn
		});
	}

	function handleInstanceTypeChange(newInstanceType: string) {
		if (!selectedType) return;

		onchange({
			dataType: selectedType,
			instanceType: newInstanceType || null,
			isArray: false
		});
	}
</script>

<div class="flex items-center gap-2">
	<div class="flex flex-col gap-1">
		<span class="text-xs text-muted-foreground">Type</span>
		<Select.Root type="single" value={selectedType} onValueChange={handleTypeChange}>
			<Select.Trigger class="w-32">
				{#if selectedType}
					<span>{selectedType}</span>
				{:else}
					<span class="text-muted-foreground">Select...</span>
				{/if}
			</Select.Trigger>
			<Select.Content>
				{#each allowedTypes as type (type.value)}
					<Select.Item value={type.value}>{type.label}</Select.Item>
				{/each}
			</Select.Content>
		</Select.Root>
	</div>

	<div class="flex flex-col gap-1">
		<span class="text-xs text-muted-foreground">
			{selectedType === 'entity' ? 'Entity Type' : 'Struct Type'}
		</span>
		<Input
			type="text"
			value={instanceType}
			oninput={(e) => handleInstanceTypeChange(e.currentTarget.value)}
			placeholder={selectedType === 'entity' ? 'e.g. Player' : 'e.g. WeaponDef'}
			class="w-40"
		/>
	</div>
</div>
