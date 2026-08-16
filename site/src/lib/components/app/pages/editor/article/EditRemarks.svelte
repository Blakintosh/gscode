<script lang="ts">
	import type { FunctionEditor } from '$lib/api-editor/function-editor.svelte';
	import { Button } from '$lib/components/ui/button/index.js';
	import { Textarea } from '$lib/components/ui/textarea/index.js';
	import { Separator } from '$lib/components/ui/separator/index.js';
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
		<div class="divide-y border-b mb-2">
			{#each remarks as remark, i}
				<div class="group relative">
					{#if editingIndex === i}
						<div class="flex flex-col gap-4 p-4 border rounded-lg bg-muted/30 my-2">
							<div class="flex items-center justify-between">
								<span class="font-medium text-sm">Edit Remark {i + 1}</span>
								<div class="flex gap-2">
									<div class="flex items-center border rounded-md px-1 bg-background">
										<Button
											variant="ghost"
											size="icon"
											class="h-7 w-7"
											disabled={i === 0}
											onclick={() => {
												functionEditor.moveRemark(i, 'up');
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
											disabled={i === remarks.length - 1}
											onclick={() => {
												functionEditor.moveRemark(i, 'down');
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
											functionEditor.removeRemark(i);
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

							<div class="flex flex-col gap-2">
								<Textarea
									value={remark}
									oninput={(e) => functionEditor.setRemark(i, e.currentTarget.value)}
									placeholder="Write a remark..."
									rows={2}
									class="resize-none"
								/>
								<p class="text-xs text-muted-foreground">Statement sentence in American English, ending with a period.</p>
							</div>
						</div>
					{:else}
						<button
							type="button"
							onclick={() => startEditing(i)}
							class="group/item flex items-start gap-2 text-left cursor-pointer hover:bg-muted/50 rounded-md px-3 py-2 -mx-3 transition-colors w-full"
						>
							<div class="flex-1 text-sm">
								{remark || '<empty>'}
							</div>
							<Pencil
								class="w-4 h-4 opacity-0 group-hover/item:opacity-50 transition-opacity mt-0.5 shrink-0"
							/>
						</button>
					{/if}
				</div>
			{/each}
		</div>
	{:else}
		<div class="text-sm text-muted-foreground italic mb-2">No remarks.</div>
	{/if}

	<Button
		variant="outline"
		size="sm"
		class="w-full gap-2 border-dashed"
		onclick={() => {
			functionEditor.addRemark();
			startEditing(remarks.length);
		}}
	>
		<Plus class="w-4 h-4" />
		Add Remark
	</Button>
</div>
