<script lang="ts">
	import type { FunctionEditor } from '$lib/api-editor/function-editor.svelte';
	import TriangleAlert from '@lucide/svelte/icons/triangle-alert';

	interface Props {
		functionEditor: FunctionEditor;
	}

	let { functionEditor }: Props = $props();
</script>

<div>
	<div class="type-label text-dim mb-2.5">Auto-validation</div>
	<div class="flex flex-col gap-2">
		<div class="flex items-center gap-2 font-mono text-[11px] tracking-[.1em] uppercase">
			{#if functionEditor.isValid && !functionEditor.isUnverified}
				<i aria-hidden="true" class="bg-primary block size-[6px] shrink-0"></i>
				<span class="text-primary">Valid</span>
			{:else if functionEditor.isValid && functionEditor.isUnverified}
				<i aria-hidden="true" class="bg-dim block size-[6px] shrink-0"></i>
				<span class="text-dim">Unverified</span>
			{:else if functionEditor.isVerified && !functionEditor.isValid}
				<i aria-hidden="true" class="bg-destructive block size-[6px] shrink-0"></i>
				<span class="text-destructive">Bad verification</span>
			{:else}
				<i aria-hidden="true" class="bg-destructive block size-[6px] shrink-0"></i>
				<span class="text-destructive">Problems detected</span>
			{/if}
		</div>
		{#if !functionEditor.isValid}
			<ul class="mt-1 mb-2 flex flex-col gap-2">
				{#each functionEditor.validationErrors as error}
					<li class="text-muted-foreground flex items-center gap-2 text-xs leading-tight">
						<TriangleAlert class="text-destructive size-3.5 shrink-0" />
						<span>{error}</span>
					</li>
				{/each}
			</ul>
		{/if}
		{#if functionEditor.isUnverified && !functionEditor.isValid}
			<p class="text-muted-foreground text-xs">
				Manual review required to fix incorrect documentation.
			</p>
		{/if}
		{#if functionEditor.isUnverified && functionEditor.isValid}
			<p class="text-muted-foreground text-xs">
				Auto-validation pass does not guarantee documentation is correct. Manual review recommended.
			</p>
		{/if}
		{#if functionEditor.isVerified && !functionEditor.isValid}
			<p class="text-muted-foreground text-xs">
				Function does not pass requirements for verified status.
			</p>
		{/if}
	</div>
</div>
