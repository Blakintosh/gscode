<script lang="ts">
	import * as HoverCard from '$components/ui/hover-card';

	interface Props {
		name: string;
		description: string;
		version: number;
		revisedOn: string;
	}

	let { name, description, version, revisedOn }: Props = $props();
</script>

<HoverCard.Content class="flex flex-col gap-2">
	<span class="flex items-baseline gap-2 font-mono text-sm">
		{name}
		<span class="text-primary font-mono text-[10px] tracking-[.08em]">API v{version}</span>
	</span>
	<p class="text-muted-foreground text-sm">
		{description}
	</p>
	<p class="text-dim font-mono text-[10px] tracking-[.08em]">
		API last revised on {revisedOn}
	</p>
</HoverCard.Content>
