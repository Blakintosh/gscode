<script lang="ts">
	import type { FunctionEditor } from '$lib/api-editor/function-editor.svelte';
	import { Textarea } from '$lib/components/ui/textarea/index.js';
	import * as Code from '$lib/components/ui/code/index.js';
	// @ts-ignore
	import Pencil from 'lucide-svelte/icons/pencil';
	// @ts-ignore
	import Plus from 'lucide-svelte/icons/plus';

	interface Props {
		functionEditor: FunctionEditor;
	}

	let { functionEditor }: Props = $props();
	let editing = $state(false);
	let textareaRef = $state<HTMLTextAreaElement | null>(null);

	function startEditing() {
		editing = true;
		setTimeout(() => textareaRef?.focus(), 0);
	}

	function stopEditing() {
		editing = false;
	}

	function handleKeydown(e: KeyboardEvent) {
		if (e.key === 'Escape') {
			stopEditing();
		}
	}
</script>

{#if editing}
	<div class="flex flex-col gap-2">
		<Textarea
			bind:ref={textareaRef}
			value={functionEditor.function.example ?? ''}
			oninput={(e) => functionEditor.setExample(e.currentTarget.value)}
			onblur={stopEditing}
			onkeydown={handleKeydown}
			placeholder="// Add example code here..."
			rows={8}
			class="font-mono text-sm resize-none"
		/>
		<p class="text-xs text-muted-foreground">Press Escape or click outside to finish editing</p>
	</div>
{:else if functionEditor.function.example}
	<button
		type="button"
		onclick={startEditing}
		class="group relative cursor-pointer w-full text-left"
	>
		<Code.Root value="1">
			<Code.Tabs>
				<Code.Tab value="1">Example</Code.Tab>
			</Code.Tabs>
			<Code.Example value="1">
				<Code.Block code={functionEditor.function.example} />
			</Code.Example>
		</Code.Root>
		<div
			class="absolute inset-0 bg-muted/0 group-hover:bg-muted/30 transition-colors rounded-lg flex items-center justify-center"
		>
			<div
				class="opacity-0 group-hover:opacity-100 transition-opacity bg-background/90 rounded-md px-3 py-1.5 flex items-center gap-2 text-sm"
			>
				<Pencil class="w-4 h-4" />
				Click to edit
			</div>
		</div>
	</button>
{:else}
	<button
		type="button"
		onclick={startEditing}
		class="w-full border border-dashed rounded-lg p-6 flex flex-col items-center justify-center gap-2 text-muted-foreground hover:bg-muted/50 hover:text-foreground transition-colors cursor-pointer"
	>
		<Plus class="w-6 h-6" />
		<span class="text-sm">Add example code</span>
	</button>
{/if}
