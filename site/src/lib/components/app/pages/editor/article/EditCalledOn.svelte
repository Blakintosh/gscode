<script lang="ts">
	import type { FunctionEditor } from '$lib/api-editor/function-editor.svelte';
	import type { ScrFunctionParameter } from '$lib/models/library';
	import { Input } from '$lib/components/ui/input/index.js';
	import { Textarea } from '$lib/components/ui/textarea/index.js';
	import { Checkbox } from '$lib/components/ui/checkbox/index.js';
	import { Button } from '$lib/components/ui/button/index.js';
	import CalledOnTypePicker from './CalledOnTypePicker.svelte';
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

	let calledOn: ScrFunctionParameter | null | undefined = $derived(
		functionEditor.function.overloads[overloadIndex]?.calledOn
	);

	let hasCalledOn = $derived(calledOn != null);
	let typeString = $derived(typeToString(calledOn?.type ?? undefined));

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
			<span class="font-medium text-sm">Edit Called-On Entity</span>
			<Button variant="ghost" size="sm" onclick={stopEditing}>
				<X class="w-4 h-4" />
			</Button>
		</div>

		<div class="flex items-center gap-2">
			<Checkbox
				id="has-calledon-{overloadIndex}"
				checked={hasCalledOn}
				onCheckedChange={(checked) =>
					functionEditor.setCalledOnEnabled(overloadIndex, checked === true)}
			/>
			<label
				for="has-calledon-{overloadIndex}"
				class="text-sm leading-none peer-disabled:cursor-not-allowed peer-disabled:opacity-70"
			>
				Called on an entity
			</label>
		</div>

		{#if hasCalledOn}
			<div class="flex flex-col gap-4">
				<div class="flex flex-col gap-2">
					<span class="text-sm font-medium">Type</span>
					<CalledOnTypePicker
						value={calledOn?.type}
						onchange={(type) => functionEditor.setCalledOnType(overloadIndex, type)}
					/>
				</div>

				<div class="flex flex-col gap-2">
					<label for="calledon-name-{overloadIndex}" class="text-sm font-medium">Name</label>
					<Input
						id="calledon-name-{overloadIndex}"
						type="text"
						value={calledOn?.name ?? ''}
						oninput={(e) => functionEditor.setCalledOnName(overloadIndex, e.currentTarget.value)}
						placeholder="e.g. self, player, entity"
					/>
				</div>

				<div class="flex flex-col gap-2">
					<label for="calledon-desc-{overloadIndex}" class="text-sm font-medium">Description</label>
					<Textarea
						id="calledon-desc-{overloadIndex}"
						value={calledOn?.description ?? ''}
						oninput={(e) =>
							functionEditor.setCalledOnDescription(overloadIndex, e.currentTarget.value)}
						placeholder="Describe the entity this function is called on..."
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
			{#if !hasCalledOn}
				<div class="text-sm text-muted-foreground italic">
					Not called on an entity. Click to add.
				</div>
			{:else if calledOn?.type}
				<div class="py-2">
					<div class="font-medium text-sm">
						{calledOn.name ?? 'self'}
						<span class="text-muted-foreground font-normal text-xs">
							{typeString}
						</span>
					</div>
					<div class="text-xs lg:text-sm">
						{calledOn.description ?? 'No description.'}
					</div>
				</div>
			{:else}
				<div class="text-sm text-muted-foreground italic">
					Called-on type not specified. Click to configure.
				</div>
			{/if}
		</div>
		<Pencil class="w-4 h-4 opacity-0 group-hover:opacity-50 transition-opacity mt-0.5 shrink-0" />
	</button>
{/if}
