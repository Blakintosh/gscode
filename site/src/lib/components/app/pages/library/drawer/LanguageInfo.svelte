<script lang="ts">
	import * as HoverCard from '$components/ui/hover-card';
	import { Badge } from '$lib/components/ui/badge/index.js';

	type Props = {
		name: string;
		description: string;
		version: number;
		revisedOn: string;
	};

	let { name, description, version, revisedOn }: Props = $props();
</script>

<HoverCard.Content class="w-72">
	<div class="flex items-center gap-2">
		<span class="text-foreground font-mono text-[13px]">{name}</span>
		<Badge variant="outline">API v{version}</Badge>
	</div>
	<p class="text-muted-foreground mt-2 text-[14px] font-light">
		{description}
	</p>
	<p class="text-dim mt-3 font-mono text-[10px] tracking-[.14em] uppercase">
		Revised {revisedOn}
	</p>
</HoverCard.Content>
