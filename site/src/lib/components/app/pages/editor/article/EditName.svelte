<script lang="ts">
	import type { FunctionEditor } from '$lib/api-editor/function-editor.svelte';
	import { Input } from '$lib/components/ui/input/index.js';
	// @ts-ignore
	import Pencil from 'lucide-svelte/icons/pencil';

	interface Props {
		functionEditor: FunctionEditor;
	}

	let { functionEditor }: Props = $props();
	let editing = $state(false);
	let inputRef = $state<HTMLInputElement | null>(null);

	function startEditing() {
		editing = true;
		// Focus the input after it renders
		setTimeout(() => inputRef?.focus(), 0);
	}

	function stopEditing() {
		editing = false;
	}

	function handleKeydown(e: KeyboardEvent) {
		if (e.key === 'Enter' || e.key === 'Escape') {
			stopEditing();
		}
	}
</script>

{#if editing}
	<Input
		bind:ref={inputRef}
		type="text"
		value={functionEditor.function.name}
		oninput={(e) => functionEditor.setName(e.currentTarget.value)}
		onblur={stopEditing}
		onkeydown={handleKeydown}
		class="text-xl font-bold tracking-tight lg:text-4xl h-auto py-1 px-2"
	/>
{:else}
	<button
		type="button"
		onclick={startEditing}
		class="group flex items-center gap-2 text-left cursor-pointer hover:bg-muted/50 rounded-md px-2 py-1 -mx-2 -my-1 transition-colors"
	>
		<h1 class="scroll-m-20 text-xl font-bold tracking-tight lg:text-4xl">
			{functionEditor.function.name}
		</h1>
		<Pencil class="w-4 h-4 opacity-0 group-hover:opacity-50 transition-opacity" />
	</button>
{/if}
