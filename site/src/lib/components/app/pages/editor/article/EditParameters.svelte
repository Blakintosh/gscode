<script lang="ts">
	import type { FunctionEditor } from '$lib/api-editor/function-editor.svelte';
	import { Button } from '$lib/components/ui/button/index.js';
	import { Input } from '$lib/components/ui/input/index.js';
	import { Textarea } from '$lib/components/ui/textarea/index.js';
	import { Checkbox } from '$lib/components/ui/checkbox/index.js';
	import Brush from '$lib/components/site/Brush.svelte';
	import TypePicker from './TypePicker.svelte';
	import ParameterEntry from './ParameterEntry.svelte';
	import Plus from '@lucide/svelte/icons/plus';
	import Trash2 from '@lucide/svelte/icons/trash-2';
	import Pencil from '@lucide/svelte/icons/pencil';
	import X from '@lucide/svelte/icons/x';
	import ChevronUp from '@lucide/svelte/icons/chevron-up';
	import ChevronDown from '@lucide/svelte/icons/chevron-down';

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
		<div class="border-border divide-border mb-1 divide-y border-b">
			{#each parameters as parameter, i}
				<div class="group relative">
					{#if editingIndex === i}
						<Brush
							surface="popover"
							cut={10}
							rim="edge"
							class="my-2"
							bodyClass="flex flex-col gap-4 px-4 py-4"
						>
							<div class="flex items-center justify-between gap-2">
								<span class="type-label text-dim">Parameter {i + 1}</span>
								<div class="flex items-center gap-1">
									<Button
										variant="ghost"
										size="icon-xs"
										title="Move up"
										disabled={i === 0}
										onclick={() => {
											functionEditor.moveParameter(overloadIndex, i, 'up');
											editingIndex = i - 1;
										}}
									>
										<ChevronUp />
										<span class="sr-only">Move up</span>
									</Button>
									<Button
										variant="ghost"
										size="icon-xs"
										title="Move down"
										disabled={i === parameters.length - 1}
										onclick={() => {
											functionEditor.moveParameter(overloadIndex, i, 'down');
											editingIndex = i + 1;
										}}
									>
										<ChevronDown />
										<span class="sr-only">Move down</span>
									</Button>
									<Button
										variant="ghost"
										size="icon-xs"
										title="Remove parameter"
										class="hover:text-destructive"
										onclick={() => {
											functionEditor.removeParameter(overloadIndex, i);
											stopEditing();
										}}
									>
										<Trash2 />
										<span class="sr-only">Remove parameter</span>
									</Button>
									<Button variant="ghost" size="icon-xs" title="Done" onclick={stopEditing}>
										<X />
										<span class="sr-only">Done</span>
									</Button>
								</div>
							</div>

							<div class="grid grid-cols-1 gap-4 md:grid-cols-2">
								<div class="flex flex-col gap-2">
									<label for="param-name-{i}" class="type-label text-dim">Name</label>
									<Input
										id="param-name-{i}"
										type="text"
										value={parameter.name ?? ''}
										oninput={(e) =>
											functionEditor.setParameterName(overloadIndex, i, e.currentTarget.value)}
										placeholder="Parameter name"
									/>
									<p class="text-dim font-mono text-[10px] tracking-[.06em]">
										camelCase, e.g. hasAmmo.
									</p>
								</div>

								<div class="flex flex-col gap-2">
									<span class="type-label text-dim">Type</span>
									<TypePicker
										value={parameter.type}
										onchange={(type) => functionEditor.setParameterType(overloadIndex, i, type)}
									/>
								</div>
							</div>

							<div class="flex items-center gap-2.5">
								<Checkbox
									id="param-mandatory-{i}"
									checked={parameter.mandatory ?? false}
									onCheckedChange={(checked) =>
										functionEditor.setParameterMandatory(overloadIndex, i, checked === true)}
								/>
								<label for="param-mandatory-{i}" class="text-sm leading-none">
									Mandatory (required)
								</label>
							</div>

							<div class="flex flex-col gap-2">
								<label for="param-desc-{i}" class="type-label text-dim">Description</label>
								<Textarea
									id="param-desc-{i}"
									value={parameter.description ?? ''}
									oninput={(e) =>
										functionEditor.setParameterDescription(overloadIndex, i, e.currentTarget.value)}
									placeholder="Describe this parameter..."
									rows={2}
									class="resize-none"
								/>
								<p class="text-dim font-mono text-[10px] tracking-[.06em]">
									Statement sentence in American English, ending with a period.
								</p>
							</div>
						</Brush>
					{:else}
						<button
							type="button"
							onclick={() => startEditing(i)}
							class="group/item -mx-3 flex w-full cursor-pointer items-start gap-2 px-3 py-2 text-left transition-colors hover:bg-[var(--wash-hover)]"
						>
							<div class="flex-1">
								<ParameterEntry {...parameter} />
							</div>
							<Pencil
								class="text-dim mt-2 size-4 shrink-0 opacity-0 transition-opacity group-hover/item:opacity-100"
							/>
						</button>
					{/if}
				</div>
			{/each}
		</div>
	{:else}
		<div class="text-dim mb-1 text-sm">No parameters.</div>
	{/if}

	<Button
		variant="outline"
		size="sm"
		class="w-full"
		onclick={() => {
			functionEditor.addParameter(overloadIndex);
			startEditing(parameters.length); // Edit the newly added parameter
		}}
	>
		<Plus />
		Add parameter
	</Button>
</div>
