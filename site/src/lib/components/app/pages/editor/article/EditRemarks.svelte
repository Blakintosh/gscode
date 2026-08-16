<script lang="ts">
	import type { FunctionEditor } from '$lib/api-editor/function-editor.svelte';
	import { Button } from '$lib/components/ui/button/index.js';
	import { Textarea } from '$lib/components/ui/textarea/index.js';
	import Brush from '$lib/components/site/Brush.svelte';
	import Plus from '@lucide/svelte/icons/plus';
	import Trash2 from '@lucide/svelte/icons/trash-2';
	import Pencil from '@lucide/svelte/icons/pencil';
	import X from '@lucide/svelte/icons/x';
	import ChevronUp from '@lucide/svelte/icons/chevron-up';
	import ChevronDown from '@lucide/svelte/icons/chevron-down';

	interface Props {
		functionEditor: FunctionEditor;
	}

	let { functionEditor }: Props = $props();

	let remarks = $derived(functionEditor.function.remarks ?? []);
	let editingIndex = $state<number | null>(null);

	function startEditing(index: number) {
		editingIndex = index;
	}

	function stopEditing() {
		editingIndex = null;
	}
</script>

<div class="flex flex-col gap-4">
	{#if remarks.length > 0}
		<div class="border-border divide-border mb-1 divide-y border-b">
			{#each remarks as remark, i}
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
								<span class="type-label text-dim">Remark {i + 1}</span>
								<div class="flex items-center gap-1">
									<Button
										variant="ghost"
										size="icon-xs"
										title="Move up"
										disabled={i === 0}
										onclick={() => {
											functionEditor.moveRemark(i, 'up');
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
										disabled={i === remarks.length - 1}
										onclick={() => {
											functionEditor.moveRemark(i, 'down');
											editingIndex = i + 1;
										}}
									>
										<ChevronDown />
										<span class="sr-only">Move down</span>
									</Button>
									<Button
										variant="ghost"
										size="icon-xs"
										title="Remove remark"
										class="hover:text-destructive"
										onclick={() => {
											functionEditor.removeRemark(i);
											stopEditing();
										}}
									>
										<Trash2 />
										<span class="sr-only">Remove remark</span>
									</Button>
									<Button variant="ghost" size="icon-xs" title="Done" onclick={stopEditing}>
										<X />
										<span class="sr-only">Done</span>
									</Button>
								</div>
							</div>

							<div class="flex flex-col gap-2">
								<Textarea
									value={remark}
									oninput={(e) => functionEditor.setRemark(i, e.currentTarget.value)}
									placeholder="Write a remark..."
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
							<div class="flex-1 text-sm">
								{remark || '<empty>'}
							</div>
							<Pencil
								class="text-dim mt-0.5 size-4 shrink-0 opacity-0 transition-opacity group-hover/item:opacity-100"
							/>
						</button>
					{/if}
				</div>
			{/each}
		</div>
	{:else}
		<div class="text-dim mb-1 text-sm">No remarks.</div>
	{/if}

	<Button
		variant="outline"
		size="sm"
		class="w-full"
		onclick={() => {
			functionEditor.addRemark();
			startEditing(remarks.length);
		}}
	>
		<Plus />
		Add remark
	</Button>
</div>
