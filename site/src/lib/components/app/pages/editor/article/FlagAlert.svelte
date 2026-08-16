<script lang="ts">
	import Brush from '$lib/components/site/Brush.svelte';
	import type { Component } from 'svelte';

	type FlagAlertProps = {
		Icon: Component;
		title: string;
		description: string;
	};

	let { Icon, title, description }: FlagAlertProps = $props();
</script>

<Brush surface="card" cut={10} rim="edge" bodyClass="flex items-start gap-3 px-4 py-3.5">
	<Icon class="text-muted-foreground mt-0.5 size-4 shrink-0" />
	<div class="flex flex-col gap-1">
		<span class="type-label text-dim">{title}</span>
		<p class="text-muted-foreground text-xs lg:text-sm">{description}</p>
	</div>
</Brush>
