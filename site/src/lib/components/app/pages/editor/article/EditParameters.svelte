<script lang="ts">
	import type { FunctionEditor } from '$lib/api-editor/function-editor.svelte';
	import { Button } from '$lib/components/ui/button/index.js';
	import { Input } from '$lib/components/ui/input/index.js';
	import { Textarea } from '$lib/components/ui/textarea/index.js';
	import { Checkbox } from '$lib/components/ui/checkbox/index.js';
	import { Separator } from '$lib/components/ui/separator/index.js';
	import TypePicker from './TypePicker.svelte';
	import ParameterEntry from './ParameterEntry.svelte';
	// @ts-ignore
	import Plus from 'lucide-svelte/icons/plus';
	// @ts-ignore
	import Trash2 from 'lucide-svelte/icons/trash-2';
	// @ts-ignore
	import Pencil from 'lucide-svelte/icons/pencil';
	// @ts-ignore
	import X from 'lucide-svelte/icons/x';
	// @ts-ignore
	import ChevronUp from 'lucide-svelte/icons/chevron-up';
	// @ts-ignore
	import ChevronDown from 'lucide-svelte/icons/chevron-down';

	interface Props {
		functionEditor: FunctionEditor;
		overloadIndex: number;
	}

	let { functionEditor, overloadIndex }: Props = $props();

	let parameters = $derived(functionEditor.function.overloads[overloadIndex].parameters);
	let editingIndex = $state<number | null>(null);

	function startEditing(index: number) {
		editingIndex = index;
	}

	function stopEditing() {
		editingIndex = null;
	}
</script>

<div class="flex flex-col gap-4">
	{#if parameters.length > 0}
		<div class="divide-y border-b mb-2">
			{#each parameters as parameter, i}
				<div class="group relative">
					{#if editingIndex === i}
						<div class="flex flex-col gap-4 p-4 border rounded-lg bg-muted/30 my-2">
							<div class="flex items-center justify-between">
								<span class="font-medium text-sm">Edit Parameter {i + 1}</span>
								<div class="flex gap-2">
									<div class="flex items-center border rounded-md px-1 bg-background">
										<Button
											variant="ghost"
											size="icon"
											class="h-7 w-7"
											disabled={i === 0}
											onclick={() => {
												functionEditor.moveParameter(overloadIndex, i, 'up');
												editingIndex = i - 1;
											}}
										>
											<ChevronUp class="w-4 h-4" />
										</Button>
										<Separator orientation="vertical" class="h-4" />
										<Button
											variant="ghost"
											size="icon"
											class="h-7 w-7"
											disabled={i === parameters.length - 1}
											onclick={() => {
												functionEditor.moveParameter(overloadIndex, i, 'down');
												editingIndex = i + 1;
											}}
										>
											<ChevronDown class="w-4 h-4" />
										</Button>
									</div>
									<Button
										variant="ghost"
										size="sm"
										onclick={() => {
											functionEditor.removeParameter(overloadIndex, i);
											stopEditing();
										}}
										class="text-destructive hover:text-destructive hover:bg-destructive/10"
									>
										<Trash2 class="w-4 h-4" />
									</Button>
									<Button variant="ghost" size="sm" onclick={stopEditing}>
										<X class="w-4 h-4" />
									</Button>
								</div>
							</div>

							<div class="grid grid-cols-1 md:grid-cols-2 gap-4">
								<div class="flex flex-col gap-2">
									<label for="param-name-{i}" class="text-sm font-medium">Name</label>
									<Input
										id="param-name-{i}"
										type="text"
										value={parameter.name ?? ''}
										oninput={(e) =>
											functionEditor.setParameterName(overloadIndex, i, e.currentTarget.value)}
										placeholder="Parameter name"
									/>
								</div>

								<div class="flex flex-col gap-2">
									<span class="text-sm font-medium">Type</span>
									<TypePicker
										value={parameter.type}
										onchange={(type) => functionEditor.setParameterType(overloadIndex, i, type)}
									/>
								</div>
							</div>

							<div class="flex items-center gap-2">
								<Checkbox
									id="param-mandatory-{i}"
									checked={parameter.mandatory ?? false}
									onCheckedChange={(checked) =>
										functionEditor.setParameterMandatory(overloadIndex, i, checked === true)}
								/>
								<label
									for="param-mandatory-{i}"
									class="text-sm leading-none peer-disabled:cursor-not-allowed peer-disabled:opacity-70"
								>
									Mandatory (required)
								</label>
							</div>

							<div class="flex flex-col gap-2">
								<label for="param-desc-{i}" class="text-sm font-medium">Description</label>
								<Textarea
									id="param-desc-{i}"
									value={parameter.description ?? ''}
									oninput={(e) =>
										functionEditor.setParameterDescription(overloadIndex, i, e.currentTarget.value)}
									placeholder="Describe this parameter..."
									rows={2}
									class="resize-none"
								/>
							</div>
						</div>
					{:else}
						<button
							type="button"
							onclick={() => startEditing(i)}
							class="group/item flex items-start gap-2 text-left cursor-pointer hover:bg-muted/50 rounded-md px-3 py-2 -mx-3 transition-colors w-full"
						>
							<div class="flex-1">
								<ParameterEntry {...parameter} />
							</div>
							<Pencil
								class="w-4 h-4 opacity-0 group-hover/item:opacity-50 transition-opacity mt-2 shrink-0"
							/>
						</button>
					{/if}
				</div>
			{/each}
		</div>
	{:else}
		<div class="text-sm text-muted-foreground italic mb-2">No parameters.</div>
	{/if}

	<Button
		variant="outline"
		size="sm"
		class="w-full gap-2 border-dashed"
		onclick={() => {
			functionEditor.addParameter(overloadIndex);
			startEditing(parameters.length); // Edit the newly added parameter
		}}
	>
		<Plus class="w-4 h-4" />
		Add Parameter
	</Button>
</div>

