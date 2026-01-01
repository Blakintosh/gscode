<script lang="ts">
	import type { FunctionEditor } from '$lib/api-editor/function-editor.svelte';
	// @ts-ignore
	import TriangleAlert from 'lucide-svelte/icons/triangle-alert';

	interface Props {
		functionEditor: FunctionEditor;
	}

	let { functionEditor }: Props = $props();
</script>

<div>
	<div class="font-medium text-sm mb-2">Auto-Validation Status</div>
	<div class="flex flex-col gap-2">
		<div class="flex items-center gap-2 text-sm uppercase tracking-wider text-muted-foreground">
			{#if functionEditor.isValid && !functionEditor.isUnverified}
				<div class="size-3 rounded-full bg-green-500"></div>
				<span>Valid</span>
			{:else if functionEditor.isValid && functionEditor.isUnverified}
				<div class="size-3 rounded-full bg-yellow-400"></div>
				<span>Unverified</span>
			{:else if functionEditor.isVerified && !functionEditor.isValid}
				<div
					class="size-3 rounded-full {functionEditor.isVerified ? 'bg-red-600' : 'bg-amber-700'}"
				></div>
				<span class="text-foreground font-medium">Bad Verification</span>
			{:else}
				<div class="size-3 rounded-full bg-red-500"></div>
				<span class="text-foreground font-medium">Problems Detected</span>
			{/if}
		</div>
		{#if !functionEditor.isValid}
			<ul class="flex flex-col gap-2 mt-1 mb-2">
				{#each functionEditor.validationErrors as error}
					<li class="text-sm text-muted-foreground leading-tight flex gap-2 items-center">
						<TriangleAlert class="size-3 shrink-0 text-red-500" />
						<span>{error}</span>
					</li>
				{/each}
			</ul>
		{/if}
		{#if functionEditor.isUnverified && !functionEditor.isValid}
			<p class="text-sm text-muted-foreground">
				Manual review required to fix incorrect documentation.
			</p>
		{/if}
		{#if functionEditor.isUnverified && functionEditor.isValid}
			<p class="text-sm text-muted-foreground">
				Auto-validation pass does not guarantee documentation is correct. Manual review recommended.
			</p>
		{/if}
		{#if functionEditor.isVerified && !functionEditor.isValid}
			<p class="text-sm text-muted-foreground">
				Function does not pass requirements for verified status.
			</p>
		{/if}
	</div>
</div>

