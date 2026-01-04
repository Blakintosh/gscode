<script lang="ts">
	import { type ScrDataType, ScrEntityTypes } from '$lib/models/library';
	import * as Select from '$lib/components/ui/select/index.js';
	import { Input } from '$lib/components/ui/input/index.js';
	import { Button } from '$lib/components/ui/button/index.js';
	// @ts-ignore
	import X from 'lucide-svelte/icons/x';

	interface Props {
		value: ScrDataType;
		onchange: (type: ScrDataType) => void;
		onremove?: () => void;
		showRemove?: boolean;
	}

	let { value, onchange, onremove, showRemove = false }: Props = $props();

	// Data type categories
	const primitiveTypes = [
		{ value: 'int', label: 'int' },
		{ value: 'float', label: 'float' },
		{ value: 'number', label: 'number' },
		{ value: 'string', label: 'string' },
		{ value: 'istring', label: 'istring' },
		{ value: 'bool', label: 'bool' },
		{ value: 'vector', label: 'vector' },
		{ value: 'struct', label: 'struct' }
	];

	const complexTypes = [
		{ value: 'entity', label: 'entity' },
		{ value: 'class', label: 'class' },
		{ value: 'enum', label: 'enum' }
	];

	const specialTypes = [
		{ value: 'any', label: 'any' },
		{ value: 'vararg', label: 'vararg' }
	];

	// Entity type options for dropdown
	const entityTypeOptions = [
		{ value: '', label: 'any' },
		...ScrEntityTypes.map((t) => ({ value: t, label: t }))
	];

	// Derived state
	let selectedType = $derived(value?.dataType ?? '');
	let subType = $derived(value?.subType ?? value?.instanceType ?? '');

	let needsEntitySubType = $derived(selectedType === 'entity');
	let needsEnumSubType = $derived(selectedType === 'enum');

	function handleTypeChange(newType: string | undefined) {
		if (!newType) {
			return;
		}

		const preserveSubType = newType === 'entity' || newType === 'enum';
		onchange({
			dataType: newType,
			instanceType: preserveSubType ? subType || null : null,
			subType: preserveSubType ? subType || null : null,
			isArray: false
		});
	}

	function handleSubTypeChange(newSubType: string | undefined) {
		if (!selectedType) {
			return;
		}

		onchange({
			dataType: selectedType,
			instanceType: newSubType || null,
			subType: newSubType || null,
			isArray: false
		});
	}
</script>

<div class="flex items-center gap-2">
	<div class="flex flex-col gap-1">
		<span class="text-xs text-muted-foreground">Data Type</span>
		<Select.Root type="single" value={selectedType} onValueChange={handleTypeChange}>
			<Select.Trigger class="w-32">
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

	{#if needsEntitySubType}
		<div class="flex flex-col gap-1">
			<span class="text-xs text-muted-foreground">Entity Type</span>
			<Select.Root type="single" value={subType} onValueChange={handleSubTypeChange}>
				<Select.Trigger class="w-32">
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
	{:else if needsEnumSubType}
		<div class="flex flex-col gap-1">
			<span class="text-xs text-muted-foreground">Enum Type</span>
			<Input
				type="text"
				value={subType}
				oninput={(e) => handleSubTypeChange(e.currentTarget.value)}
				placeholder="e.g. WeaponType"
				class="w-32"
			/>
		</div>
	{/if}

	{#if showRemove && onremove}
		<Button
			variant="ghost"
			size="sm"
			class="h-8 w-8 p-0 mt-4 text-muted-foreground hover:text-destructive"
			onclick={onremove}
		>
			<X class="h-4 w-4" />
		</Button>
	{/if}
</div>
