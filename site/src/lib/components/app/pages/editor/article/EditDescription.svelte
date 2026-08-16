<script lang="ts">
	import type { FunctionEditor } from '$lib/api-editor/function-editor.svelte';
	import { Textarea } from '$lib/components/ui/textarea/index.js';
	import Pencil from '@lucide/svelte/icons/pencil';

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
	<div class="flex flex-col gap-1.5">
		<Textarea
			bind:ref={textareaRef}
			value={functionEditor.function.description ?? ''}
			oninput={(e) => functionEditor.setDescription(e.currentTarget.value)}
			onblur={stopEditing}
			onkeydown={handleKeydown}
			placeholder="Add a description..."
			rows={2}
			class="resize-none text-base"
		/>
		<p class="text-dim font-mono text-[11px] tracking-[.06em]">
			Statement sentence in American English, ending with a period.
		</p>
	</div>
{:else}
	<button
		type="button"
		onclick={startEditing}
		class="group -mx-2 -my-1 flex w-full cursor-pointer items-start gap-2.5 px-2 py-1 text-left transition-colors hover:bg-[var(--wash-hover)]"
	>
		<h2 class="text-muted-foreground flex-1 text-base leading-relaxed lg:text-[16.5px]">
			{#if functionEditor.function.description}
				{functionEditor.function.description}
			{:else}
				<span class="text-dim">No description. Click to add one.</span>
			{/if}
		</h2>
		<Pencil
			class="text-dim mt-1 size-4 shrink-0 opacity-0 transition-opacity group-hover:opacity-100"
		/>
	</button>
{/if}
