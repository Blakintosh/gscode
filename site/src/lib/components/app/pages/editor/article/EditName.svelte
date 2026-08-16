<script lang="ts">
	import type { FunctionEditor } from '$lib/api-editor/function-editor.svelte';
	import { Input } from '$lib/components/ui/input/index.js';
	import Pencil from '@lucide/svelte/icons/pencil';

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
	<div class="flex flex-col gap-1.5">
		<Input
			bind:ref={inputRef}
			type="text"
			value={functionEditor.function.name}
			oninput={(e) => functionEditor.setName(e.currentTarget.value)}
			onblur={stopEditing}
			onkeydown={handleKeydown}
			class="h-auto py-2 text-xl lg:text-3xl"
		/>
		<p class="text-dim font-mono text-[11px] tracking-[.06em]">
			PascalCase. Lowercase subsequent initials, e.g. IPrintLnBold, SetLpf.
		</p>
	</div>
{:else}
	<button
		type="button"
		onclick={startEditing}
		class="group -mx-2 -my-1 flex cursor-pointer items-center gap-2.5 px-2 py-1 text-left transition-colors hover:bg-[var(--wash-hover)]"
	>
		<h1 class="scroll-m-20 font-mono text-xl tracking-[-.01em] lg:text-3xl">
			{functionEditor.function.name}
		</h1>
		<Pencil class="text-dim size-4 opacity-0 transition-opacity group-hover:opacity-100" />
	</button>
{/if}
