<script lang="ts">
	import { type ScrDataType } from '$lib/models/library';
	import { Checkbox } from '$lib/components/ui/checkbox/index.js';
	import { Button } from '$lib/components/ui/button/index.js';
	import SingleTypePicker from './SingleTypePicker.svelte';
	// @ts-ignore
	import Plus from 'lucide-svelte/icons/plus';

	interface Props {
		value: ScrDataType | null | undefined;
		onchange: (type: ScrDataType | null) => void;
	}

	let { value, onchange }: Props = $props();

	// Normalize the value into a list of types for the UI
	// If unionOf exists and has items, use those as the types
	// Otherwise, treat the value itself as a single type
	let types = $derived.by(() => {
		if (!value) {
			return [{ dataType: '', isArray: false }];
		}

		if (value.unionOf && value.unionOf.length > 0) {
			return value.unionOf;
		}

		return [value];
	});

	let isArray = $derived(value?.isArray ?? false);
	let isUnion = $derived(types.length > 1);

	function normalizeType(t: ScrDataType): ScrDataType {
		// Ensure both instanceType and subType are set for backwards compatibility
		const sub = t.subType ?? t.instanceType ?? null;
		return {
			dataType: t.dataType,
			instanceType: sub,
			subType: sub,
			isArray: t.isArray ?? false,
			unionOf: t.unionOf ?? null
		};
	}

	function emitChange(newTypes: ScrDataType[], newIsArray: boolean) {
		if (newTypes.length === 0 || !newTypes[0].dataType) {
			onchange(null);
			return;
		}

		const normalizedTypes = newTypes.map(normalizeType);

		if (normalizedTypes.length === 1) {
			// Single type - no union
			onchange({
				...normalizedTypes[0],
				isArray: newIsArray,
				unionOf: null
			});
		} else {
			// Union - store all types in unionOf, use first type's dataType for compat
			onchange({
				dataType: normalizedTypes[0].dataType,
				instanceType: normalizedTypes[0].instanceType,
				subType: normalizedTypes[0].subType,
				isArray: newIsArray,
				unionOf: normalizedTypes
			});
		}
	}

	function handleTypeChange(index: number, newType: ScrDataType) {
		const newTypes = [...types];
		newTypes[index] = newType;
		emitChange(newTypes, isArray);
	}

	function handleRemoveType(index: number) {
		if (types.length <= 1) {
			return;
		}
		const newTypes = types.filter((_, i) => i !== index);
		emitChange(newTypes, isArray);
	}

	function handleAddType() {
		const newTypes = [...types, { dataType: 'string', isArray: false }];
		emitChange(newTypes, isArray);
	}

	function handleArrayChange(checked: boolean) {
		emitChange([...types], checked);
	}
</script>

<div class="flex flex-col gap-3">
	<div class="flex flex-col gap-2">
		{#each types as type, index (index)}
			{#if index > 0}
				<div class="text-xs text-muted-foreground font-medium">OR</div>
			{/if}
			<SingleTypePicker
				value={type}
				onchange={(newType) => handleTypeChange(index, newType)}
				onremove={() => handleRemoveType(index)}
				showRemove={types.length > 1}
			/>
		{/each}

		<Button
			variant="outline"
			size="sm"
			class="w-fit text-xs"
			onclick={handleAddType}
		>
			<Plus class="h-3 w-3 mr-1" />
			Add alternative type
		</Button>
	</div>

	<div class="flex items-center gap-2">
		<Checkbox
			id="is-array"
			checked={isArray}
			onCheckedChange={(checked) => handleArrayChange(checked === true)}
			disabled={!types[0]?.dataType}
		/>
		<label
			for="is-array"
			class="text-sm leading-none peer-disabled:cursor-not-allowed peer-disabled:opacity-70"
		>
			Array
		</label>
	</div>
</div>
