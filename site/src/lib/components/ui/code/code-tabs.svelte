<script module lang="ts">
    export type CodeTabsProps = TabsPrimitive.ListProps & {
        text: string;
    };
</script>

<script lang="ts">
    import { Tabs as TabsPrimitive } from "bits-ui";
    import * as Tabs from "$components/ui/tabs/index.js";
	import { CopyButton } from "$components/ui/copy-button/index.js";

	import { cn } from "$lib/utils";
	// @ts-ignore
	import Copy from "lucide-svelte/icons/copy";
	// @ts-ignore
	import Check from "lucide-svelte/icons/check";

    let {
        class: className,
        children,
        ...restProps
    }: CodeTabsProps = $props();
</script>

<Tabs.List 
    class={cn(
        "py-0 rounded-t-md rounded-b-none w-full justify-start p-0 px-1 pt-0.5 pb-0 bg-muted h-auto", 
        className
    )} 
    {...restProps}
>
    <div class="px-2 shrink-0">
        <img src="/images/file-gsc.png" alt="Language Icon" class="w-6 h-6 -my-2" />
    </div>
    {@render children?.()}
    <div class="grow bg-muted flex justify-end">
        <!-- <CopyButton variant="ghost" size={"sm"} class="px-2" text={"foo"}>
            {#snippet child({ copied })}
                {#if copied}
                    <Check class="w-4 h-4 -my-2"/>
                {:else}
                    <Copy class="w-4 h-4 -my-2"/>
                {/if}
            {/snippet}
        </CopyButton> -->
    </div>
</Tabs.List>