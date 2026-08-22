<script lang="ts">
	import type { FunctionEditor } from '$lib/api-editor/function-editor.svelte';
	import type { ScrFunctionParameter } from '$lib/models/library';
	import { Input } from '$lib/components/ui/input/index.js';
	import { Textarea } from '$lib/components/ui/textarea/index.js';
	import { Checkbox } from '$lib/components/ui/checkbox/index.js';
	import { Button } from '$lib/components/ui/button/index.js';
	import Brush from '$lib/components/site/Brush.svelte';
	import CalledOnTypePicker from './CalledOnTypePicker.svelte';
	import { typeToString } from '$lib/util/scriptApi';
	import Pencil from '@lucide/svelte/icons/pencil';
	import X from '@lucide/svelte/icons/x';

	interface Props {
		functionEditor: FunctionEditor;
		overloadIndex: number;
	}

	let { functionEditor, overloadIndex }: Props = $props();

	let editing = $state(false);

	let calledOn: ScrFunctionParameter | null | undefined = $derived(
		functionEditor.function.overloads[overloadIndex]?.calledOn
	);

	let hasCalledOn = $derived(calledOn != null);
	let typeString = $derived(typeToString(calledOn?.type ?? undefined));

	function startEditing() {
		editing = true;
	}

	function stopEditing() {
		editing = false;
	}
</script>

{#if editing}
	<Brush
		surface="popover"
		cut={10}
		rim="edge"
		bodyClass="flex flex-col gap-4 px-4 py-4"
	>
		<div class="flex items-center justify-between">
			<span class="type-label text-dim">Edit called-on entity</span>
			<Button variant="ghost" size="icon-xs" onclick={stopEditing}>
				<X />
				<span class="sr-only">Done</span>
			</Button>
		</div>

		<div class="flex items-center gap-2.5">
			<Checkbox
				id="has-calledon-{overloadIndex}"
				checked={hasCalledOn}
				onCheckedChange={(checked) =>
					functionEditor.setCalledOnEnabled(overloadIndex, checked === true)}
			/>
			<label for="has-calledon-{overloadIndex}" class="text-sm leading-none">
				Called on an entity
			</label>
		</div>

		{#if hasCalledOn}
			<div class="flex flex-col gap-4">
				<div class="flex flex-col gap-2">
					<span class="type-label text-dim">Type</span>
					<CalledOnTypePicker
						value={calledOn?.type}
						onchange={(type) => functionEditor.setCalledOnType(overloadIndex, type)}
					/>
				</div>

				<div class="flex flex-col gap-2">
					<label for="calledon-name-{overloadIndex}" class="type-label text-dim">Name</label>
					<Input
						id="calledon-name-{overloadIndex}"
						type="text"
						value={calledOn?.name ?? ''}
						oninput={(e) => functionEditor.setCalledOnName(overloadIndex, e.currentTarget.value)}
						placeholder="e.g. self, player, entity"
					/>
					<p class="text-dim font-mono text-2xs tracking-wider">camelCase, e.g. hasAmmo.</p>
				</div>

				<div class="flex flex-col gap-2">
					<label for="calledon-desc-{overloadIndex}" class="type-label text-dim">Description</label>
					<Textarea
						id="calledon-desc-{overloadIndex}"
						value={calledOn?.description ?? ''}
						oninput={(e) =>
							functionEditor.setCalledOnDescription(overloadIndex, e.currentTarget.value)}
						placeholder="Describe the entity this function is called on..."
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
			{#if !hasCalledOn}
				<div class="text-dim text-sm">Not called on an entity. Click to add.</div>
			{:else if calledOn?.type}
				<div class="flex flex-col gap-1">
					<div class="flex items-baseline gap-2 font-mono text-sm">
						<span class="text-foreground">{calledOn.name ?? 'self'}</span>
						<span class="text-primary text-xs">{typeString}</span>
					</div>
					<div class="text-muted-foreground text-xs lg:text-sm">
						{calledOn.description ?? 'No description.'}
					</div>
				</div>
			{:else}
				<div class="text-dim text-sm">Called-on type not specified. Click to configure.</div>
			{/if}
		</div>
		<Pencil
			class="text-dim mt-0.5 size-4 shrink-0 opacity-0 transition-opacity group-hover:opacity-100"
		/>
	</button>
{/if}
