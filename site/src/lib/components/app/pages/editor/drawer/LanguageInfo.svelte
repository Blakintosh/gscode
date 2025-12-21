<script lang="ts">
	import * as HoverCard from '$components/ui/hover-card';
    import { Badge } from "$lib/components/ui/badge/index.js";

    export let name: string;
    export let description: string;
    export let version: number;
    export let revisedOn: string;

</script>

<HoverCard.Content>
    <span class="flex gap-2 items-center">
        {name} <Badge color="green">API v{version}</Badge>
    </span>
    <p>
        {description}
    </p>
    <p class="text-sm text-muted-foreground">
        API last revised on {revisedOn}
    </p>
</HoverCard.Content>
