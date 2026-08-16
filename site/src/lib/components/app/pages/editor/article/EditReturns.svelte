<script lang="ts">
	import type { FunctionEditor } from '$lib/api-editor/function-editor.svelte';
	import type { ScrReturnValue } from '$lib/models/library';
	import { Input } from '$lib/components/ui/input/index.js';
	import { Textarea } from '$lib/components/ui/textarea/index.js';
	import { Checkbox } from '$lib/components/ui/checkbox/index.js';
	import { Button } from '$lib/components/ui/button/index.js';
	import Brush from '$lib/components/site/Brush.svelte';
	import TypePicker from './TypePicker.svelte';
	import { typeToString } from '$lib/util/scriptApi';
	import Pencil from '@lucide/svelte/icons/pencil';
	import X from '@lucide/svelte/icons/x';

	interface Props {
		functionEditor: FunctionEditor;
		overloadIndex: number;
	}

	let { functionEditor, overloadIndex }: Props = $props();

	let editing = $state(false);

	let returns: ScrReturnValue | null | undefined = $derived(
		functionEditor.function.overloads[overloadIndex]?.returns
	);

	let isVoid = $derived(returns?.void ?? false);
	let typeString = $derived(typeToString(returns?.type));

	function startEditing() {
		editing = true;
	}

	function stopEditing() {
		editing = false;
	}
</script>

{#if editing}
	<Brush surface="popover" cut={10} rim="edge" bodyClass="flex flex-col gap-4 px-4 py-4">
		<div class="flex items-center justify-between">
			<span class="type-label text-dim">Edit return value</span>
			<Button variant="ghost" size="icon-xs" onclick={stopEditing}>
				<X />
				<span class="sr-only">Done</span>
			</Button>
		</div>

		<div class="flex items-center gap-2.5">
			<Checkbox
				id="is-void-{overloadIndex}"
				checked={isVoid}
				onCheckedChange={(checked) => functionEditor.setReturnsVoid(overloadIndex, checked === true)}
			/>
			<label for="is-void-{overloadIndex}" class="text-sm leading-none">
				Returns void (no value)
			</label>
		</div>

		{#if !isVoid}
			<div class="flex flex-col gap-4">
				<div class="flex flex-col gap-2">
					<span class="type-label text-dim">Type</span>
					<TypePicker
						value={returns?.type}
						onchange={(type) => functionEditor.setReturnsType(overloadIndex, type)}
					/>
				</div>

				<div class="flex flex-col gap-2">
					<label for="return-name-{overloadIndex}" class="type-label text-dim">Name</label>
					<Input
						id="return-name-{overloadIndex}"
						type="text"
						value={returns?.name ?? ''}
						oninput={(e) => functionEditor.setReturnsName(overloadIndex, e.currentTarget.value)}
						placeholder="e.g. result, player, success"
					/>
					<p class="text-dim font-mono text-2xs tracking-wider">camelCase, e.g. hasAmmo.</p>
				</div>

				<div class="flex flex-col gap-2">
					<label for="return-desc-{overloadIndex}" class="type-label text-dim">Description</label>
					<Textarea
						id="return-desc-{overloadIndex}"
						value={returns?.description ?? ''}
						oninput={(e) =>
							functionEditor.setReturnsDescription(overloadIndex, e.currentTarget.value)}
						placeholder="Describe what is returned..."
						rows={2}
						class="resize-none"
					/>
					<p class="text-dim font-mono text-2xs tracking-wider">
						Statement sentence in American English, ending with a period.
					</p>
				</div>
			</div>
		{/if}
	</Brush>
{:else}
	<button
		type="button"
		onclick={startEditing}
		class="group -mx-3 flex w-full cursor-pointer items-start gap-2 px-3 py-2 text-left transition-colors hover:bg-[var(--wash-hover)]"
	>
		<div class="flex-1">
			{#if isVoid}
				<div class="text-muted-foreground text-xs lg:text-sm">
					This function does not return a value.
				</div>
			{:else if returns?.type}
				<div class="flex flex-col gap-1">
					<div class="flex items-baseline gap-2 font-mono text-sm">
						<span class="text-foreground">{returns.name ?? 'unknown'}</span>
						<span class="text-primary text-xs">{typeString}</span>
					</div>
					<div class="text-muted-foreground text-xs lg:text-sm">
						{returns.description ?? 'No description.'}
					</div>
				</div>
			{:else}
				<div class="text-dim text-sm">Return type not specified. Click to add.</div>
			{/if}
		</div>
		<Pencil
			class="text-dim mt-0.5 size-4 shrink-0 opacity-0 transition-opacity group-hover:opacity-100"
		/>
	</button>
{/if}
