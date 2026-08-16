<script lang="ts">
	import type { ScrFunctionParameter } from '$lib/models/library';
	import { typeToString } from '$lib/util/scriptApi';

	let { name, description, mandatory, type, variadic }: ScrFunctionParameter = $props();

	const typeLabel = $derived(type && type.dataType !== 'undefined' ? typeToString(type) : '');
	/** Return values have no name — the type becomes the row's subject. */
	const displayName = $derived(name ?? (typeLabel || 'value'));
</script>

<!-- One row per parameter: mono name, mono type, Sora description. Markers are mono chips. -->
<div class="border-border border-b py-3 last:border-b-0">
	<div class="flex flex-wrap items-baseline gap-x-3 gap-y-1">
		<span class="text-foreground font-mono text-sm">{displayName}</span>
		{#if typeLabel && name}
			<span class="text-dim font-mono text-xs">{typeLabel}</span>
		{/if}
		<span class="ml-auto flex shrink-0 items-center gap-2">
			{#if variadic}
				<span class="text-dim font-mono text-2xs tracking-label uppercase">variadic</span>
			{/if}
			{#if mandatory}
				<span class="text-primary font-mono text-2xs tracking-label uppercase">required</span>
			{:else if mandatory !== undefined && mandatory !== null}
				<span class="text-dim font-mono text-2xs tracking-label uppercase">optional</span>
			{/if}
		</span>
	</div>
	<p class="text-muted-foreground mt-1.5 max-w-[72ch] text-sm leading-relaxed font-light">
		{description ?? 'No description.'}
	</p>
</div>
