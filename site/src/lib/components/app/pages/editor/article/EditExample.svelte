<script lang="ts">
	import type { FunctionEditor } from '$lib/api-editor/function-editor.svelte';
	import { Textarea } from '$lib/components/ui/textarea/index.js';
	import * as Code from '$lib/components/ui/code/index.js';
	import { getEditorContext } from '$lib/api-editor/editor.svelte';
	import Pencil from '@lucide/svelte/icons/pencil';
	import Plus from '@lucide/svelte/icons/plus';

	interface Props {
		functionEditor: FunctionEditor;
	}

	let { functionEditor }: Props = $props();
	let editing = $state(false);
	let textareaRef = $state<HTMLTextAreaElement | null>(null);

	const editor = getEditorContext();
	let language = $derived(editor.library?.languageId === 'csc' ? 'CSC' : 'GSC');

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
	<div class="flex flex-col gap-1.5">
		<Textarea
			bind:ref={textareaRef}
			value={functionEditor.function.example ?? ''}
			oninput={(e) => functionEditor.setExample(e.currentTarget.value)}
			onblur={stopEditing}
			onkeydown={handleKeydown}
			placeholder="// Add example code here..."
			rows={8}
			class="resize-none font-mono text-xs"
		/>
		<p class="text-dim font-mono text-[10px] tracking-[.06em]">
			Press Escape or click outside to finish editing
		</p>
	</div>
{:else if functionEditor.function.example}
	<button type="button" onclick={startEditing} class="group relative w-full cursor-pointer text-left">
		<Code.Root value="example" tab="example" {language}>
			<Code.Example value="example">
				<Code.Block code={functionEditor.function.example} />
			</Code.Example>
		</Code.Root>
		<div class="absolute inset-0 flex items-center justify-center">
			<span
				class="chip-cut bg-popover text-foreground inset-edge flex items-center gap-2 px-3 py-2 font-mono text-[10px] tracking-[.13em] uppercase opacity-0 transition-opacity group-hover:opacity-100"
			>
				<Pencil class="size-3.5" />
				Click to edit
			</span>
		</div>
	</button>
{:else}
	<button
		type="button"
		onclick={startEditing}
		class="border-border text-dim hover:text-primary flex w-full cursor-pointer flex-col items-center justify-center gap-2 border border-dashed p-6 transition-colors hover:bg-[var(--wash-hover)]"
	>
		<Plus class="size-5" />
		<span class="font-mono text-[10px] tracking-[.13em] uppercase">Add example code</span>
	</button>
{/if}
