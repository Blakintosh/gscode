<script lang="ts">
	import type { FunctionEditor } from '$lib/api-editor/function-editor.svelte';
	import { Textarea } from '$lib/components/ui/textarea/index.js';
	// @ts-ignore
	import Pencil from 'lucide-svelte/icons/pencil';

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
		// Allow Enter for newlines in textarea, use Escape or blur to finish
	}
</script>

{#if editing}
	<Textarea
		bind:ref={textareaRef}
		value={functionEditor.function.description ?? ''}
		oninput={(e) => functionEditor.setDescription(e.currentTarget.value)}
		onblur={stopEditing}
		onkeydown={handleKeydown}
		placeholder="Add a description..."
		rows={2}
		class="text-base lg:text-xl text-muted-foreground resize-none"
	/>
{:else}
	<button
		type="button"
		onclick={startEditing}
		class="group flex items-start gap-2 text-left cursor-pointer hover:bg-muted/50 rounded-md px-2 py-1 -mx-2 -my-1 transition-colors w-full"
	>
		<h2 class="text-base lg:text-xl text-muted-foreground flex-1">
			{#if functionEditor.function.description}
				{functionEditor.function.description}
			{:else}
				<span class="italic opacity-50">No description. Click to add one.</span>
			{/if}
		</h2>
		<Pencil class="w-4 h-4 opacity-0 group-hover:opacity-50 transition-opacity mt-1 shrink-0" />
	</button>
{/if}
