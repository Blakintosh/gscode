<script lang="ts">
	import type { ScrDataType } from '$lib/models/library';
	import * as Select from '$lib/components/ui/select/index.js';
	import { Input } from '$lib/components/ui/input/index.js';
	import { Checkbox } from '$lib/components/ui/checkbox/index.js';

	interface Props {
		value: ScrDataType | null | undefined;
		onchange: (type: ScrDataType | null) => void;
	}

	let { value, onchange }: Props = $props();

	// Data type categories
	const primitiveTypes = [
		{ value: 'int', label: 'int' },
		{ value: 'float', label: 'float' },
		{ value: 'number', label: 'number' },
		{ value: 'string', label: 'string' },
		{ value: 'bool', label: 'bool' },
		{ value: 'vec3', label: 'vec3' }
	];

	const complexTypes = [
		{ value: 'struct', label: 'struct', hasInstanceType: true },
		{ value: 'entity', label: 'entity', hasInstanceType: true }
	];

	const specialTypes = [
		{ value: 'any', label: 'any' },
		{ value: 'enum', label: 'enum', hasInstanceType: true },
		{ value: 'map', label: 'map' }
	];

	// Derived state from value
	let selectedType = $derived(value?.dataType ?? '');
	let instanceType = $derived(value?.instanceType ?? '');
	let isArray = $derived(value?.isArray ?? false);

	// Check if current type needs an instance type input
	let needsInstanceType = $derived(
		complexTypes.some((t) => t.value === selectedType && t.hasInstanceType) ||
			specialTypes.some((t) => t.value === selectedType && t.hasInstanceType)
	);

	function handleTypeChange(newType: string | undefined) {
		if (!newType) {
			onchange(null);
			return;
		}

		onchange({
			dataType: newType,
			instanceType: needsInstanceType ? instanceType : null,
			isArray
		});
	}

	function handleInstanceTypeChange(newInstanceType: string) {
		if (!selectedType) return;

		onchange({
			dataType: selectedType,
			instanceType: newInstanceType || null,
			isArray
		});
	}

	function handleArrayChange(checked: boolean) {
		if (!selectedType) return;

		onchange({
			dataType: selectedType,
			instanceType: needsInstanceType ? instanceType : null,
			isArray: checked
		});
	}
</script>

<div class="flex flex-col gap-3">
	<div class="flex flex-col gap-2">
		<div class="flex items-center gap-2">
			<div class="flex flex-col gap-1">
				<span class="text-xs text-muted-foreground">Data Type</span>
				<Select.Root type="single" value={selectedType} onValueChange={handleTypeChange}>
					<Select.Trigger class="w-40">
						{#if selectedType}
							<span>{selectedType}</span>
						{:else}
							<span class="text-muted-foreground">Select...</span>
						{/if}
					</Select.Trigger>
					<Select.Content>
						<Select.Group>
							<Select.GroupHeading>Primitives</Select.GroupHeading>
							{#each primitiveTypes as type (type.value)}
								<Select.Item value={type.value}>{type.label}</Select.Item>
							{/each}
						</Select.Group>
						<Select.Separator />
						<Select.Group>
							<Select.GroupHeading>Complex</Select.GroupHeading>
							{#each complexTypes as type (type.value)}
								<Select.Item value={type.value}>{type.label}</Select.Item>
							{/each}
						</Select.Group>
						<Select.Separator />
						<Select.Group>
							<Select.GroupHeading>Special</Select.GroupHeading>
							{#each specialTypes as type (type.value)}
								<Select.Item value={type.value}>{type.label}</Select.Item>
							{/each}
						</Select.Group>
					</Select.Content>
				</Select.Root>
			</div>

			{#if needsInstanceType}
				<div class="flex flex-col gap-1">
					<span class="text-xs text-muted-foreground">
						{selectedType === 'entity' ? 'Entity Type' : selectedType === 'enum' ? 'Enum Type' : 'Struct Type'}
					</span>
					<Input
						type="text"
						value={instanceType}
						oninput={(e) => handleInstanceTypeChange(e.currentTarget.value)}
						placeholder={selectedType === 'entity' ? 'e.g. Player' : 'e.g. WeaponDef'}
						class="w-40"
					/>
				</div>
			{/if}
		</div>
	</div>

	<div class="flex items-center gap-2">
		<Checkbox
			id="is-array"
			checked={isArray}
			onCheckedChange={(checked) => handleArrayChange(checked === true)}
			disabled={!selectedType}
		/>
		<label
			for="is-array"
			class="text-sm leading-none peer-disabled:cursor-not-allowed peer-disabled:opacity-70"
		>
			Array
		</label>
	</div>
</div>
