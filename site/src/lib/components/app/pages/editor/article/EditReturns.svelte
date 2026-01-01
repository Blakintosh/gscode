<script lang="ts">
	import type { FunctionEditor } from '$lib/api-editor/function-editor.svelte';
	import type { ScrReturnValue } from '$lib/models/library';
	import { Input } from '$lib/components/ui/input/index.js';
	import { Textarea } from '$lib/components/ui/textarea/index.js';
	import { Checkbox } from '$lib/components/ui/checkbox/index.js';
	import { Button } from '$lib/components/ui/button/index.js';
	import TypePicker from './TypePicker.svelte';
	import { typeToString } from '$lib/util/scriptApi';
	// @ts-ignore
	import Pencil from 'lucide-svelte/icons/pencil';
	// @ts-ignore
	import X from 'lucide-svelte/icons/x';

	interface Props {
		functionEditor: FunctionEditor;
		overloadIndex: number;
	}

	let { functionEditor, overloadIndex }: Props = $props();

	let editing = $state(false);

	let returns: ScrReturnValue | null | undefined = $derived(
		functionEditor.function.overloads[overloadIndex]?.returns
	);

	let isVoid = $derived(returns?.void ?? false);
	let typeString = $derived(typeToString(returns?.type));

	function startEditing() {
		editing = true;
	}

	function stopEditing() {
		editing = false;
	}
</script>

{#if editing}
	<div class="flex flex-col gap-4 p-4 border rounded-lg bg-muted/30">
		<div class="flex items-center justify-between">
			<span class="font-medium text-sm">Edit Return Value</span>
			<Button variant="ghost" size="sm" onclick={stopEditing}>
				<X class="w-4 h-4" />
			</Button>
		</div>

		<div class="flex items-center gap-2">
			<Checkbox
				id="is-void-{overloadIndex}"
				checked={isVoid}
				onCheckedChange={(checked) => functionEditor.setReturnsVoid(overloadIndex, checked === true)}
			/>
			<label
				for="is-void-{overloadIndex}"
				class="text-sm leading-none peer-disabled:cursor-not-allowed peer-disabled:opacity-70"
			>
				Returns void (no value)
			</label>
		</div>

		{#if !isVoid}
			<div class="flex flex-col gap-4">
				<div class="flex flex-col gap-2">
					<span class="text-sm font-medium">Type</span>
					<TypePicker
						value={returns?.type}
						onchange={(type) => functionEditor.setReturnsType(overloadIndex, type)}
					/>
				</div>

				<div class="flex flex-col gap-2">
					<label for="return-name-{overloadIndex}" class="text-sm font-medium">Name</label>
					<Input
						id="return-name-{overloadIndex}"
						type="text"
						value={returns?.name ?? ''}
						oninput={(e) => functionEditor.setReturnsName(overloadIndex, e.currentTarget.value)}
						placeholder="e.g. result, player, success"
					/>
				</div>

				<div class="flex flex-col gap-2">
					<label for="return-desc-{overloadIndex}" class="text-sm font-medium">Description</label>
					<Textarea
						id="return-desc-{overloadIndex}"
						value={returns?.description ?? ''}
						oninput={(e) =>
							functionEditor.setReturnsDescription(overloadIndex, e.currentTarget.value)}
						placeholder="Describe what is returned..."
						rows={2}
						class="resize-none"
					/>
				</div>
			</div>
		{/if}
	</div>
{:else}
	<button
		type="button"
		onclick={startEditing}
		class="group flex items-start gap-2 text-left cursor-pointer hover:bg-muted/50 rounded-md px-3 py-2 -mx-3 -my-2 transition-colors w-full"
	>
		<div class="flex-1">
			{#if isVoid}
				<div class="text-xs lg:text-sm">This function does not return a value.</div>
			{:else if returns?.type}
				<div class="py-2">
					<div class="font-medium text-sm">
						{returns.name ?? 'unknown'}
						<span class="text-muted-foreground font-normal text-xs">
							{typeString}
						</span>
					</div>
					<div class="text-xs lg:text-sm">
						{returns.description ?? 'No description.'}
					</div>
				</div>
			{:else}
				<div class="text-sm text-muted-foreground italic">
					Return type not specified. Click to add.
				</div>
			{/if}
		</div>
		<Pencil class="w-4 h-4 opacity-0 group-hover:opacity-50 transition-opacity mt-0.5 shrink-0" />
	</button>
{/if}
