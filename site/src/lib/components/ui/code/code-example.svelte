<script module lang="ts">
    export type CodeExampleProps = TabsPrimitive.ContentProps;
</script>

<script lang="ts">
    import { Tabs as TabsPrimitive } from "bits-ui";
    import * as Tabs from "$components/ui/tabs/index.js";
    
	import { cn } from "$lib/utils";

    let {
        class: className,
        value = $bindable(),
        children,
        ...restProps
    }: CodeExampleProps = $props();
</script>

<Tabs.Content 
    class={cn(
        "m-0 [&>pre]:rounded-t-none", 
        className
    )} 
	{value}
    {...restProps}
>
    {@render children?.()}
</Tabs.Content>