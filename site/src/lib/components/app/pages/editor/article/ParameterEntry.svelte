<script lang="ts">
	import type { ScrFunctionParameter } from '$lib/models/library';
	import { typeToString } from '$lib/util/scriptApi';

	let { name, description, mandatory, type }: ScrFunctionParameter = $props();
</script>

<div class="flex flex-col gap-1">
	<div class="flex w-full items-baseline justify-between gap-3 font-mono text-sm">
		<span class="flex items-baseline gap-2">
			<span class="text-foreground">
				{name}{#if mandatory}<span class="text-destructive">*</span>{/if}
			</span>
			{#if type && type.dataType !== 'undefined'}
				<span class="text-primary text-xs">{typeToString(type)}</span>
			{/if}
		</span>

		{#if mandatory}
			<span class="text-destructive text-[11px] tracking-[.13em] uppercase">required</span>
		{:else if mandatory !== undefined}
			<span class="text-dim text-[11px] tracking-[.13em] uppercase">optional</span>
		{/if}
	</div>
	<div class="text-muted-foreground text-xs lg:text-sm">
		{description ?? 'No description.'}
	</div>
</div>
