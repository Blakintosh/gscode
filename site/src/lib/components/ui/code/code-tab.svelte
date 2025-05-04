<script module lang="ts">
    export type CodeTabProps = TabsPrimitive.TriggerProps;
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
    }: CodeTabProps = $props();
</script>

<Tabs.Trigger 
    class={cn(
        "", 
        className
    )} 
	{value}
    {...restProps}
>
    {@render children?.()}
</Tabs.Trigger>