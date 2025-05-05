<script module lang="ts">
    import { Tabs as TabsPrimitive } from "bits-ui";

    export type CodeProps = TabsPrimitive.RootProps;
</script>

<script lang="ts">
	import { cn } from '$lib/utils';
    import * as Tabs from "$components/ui/tabs/index.js";

    let { class: className, children, ...restProps }: CodeProps = $props();
</script>

<Tabs.Root class={cn(
        "", 
        className
    )} 
    {...restProps}
>
	{@render children?.()}
</Tabs.Root>
