<script lang="ts">
	import type { ScrFunctionParameter } from "$lib/models/library";
	import { typeToString } from "$lib/util/scriptApi";

    let { name, description, mandatory, type }: ScrFunctionParameter = $props();
</script>

<div class="py-2">
    <div class="font-medium text-sm inline-flex justify-between w-full">
        <span>
            {#if mandatory}
                {name}<span class="text-red-600 dark:text-red-500 font-medium">*</span>
            {:else}
                {name}
            {/if}
            <span class="text-muted-foreground font-normal text-xs">
                {#if type && type.dataType !== "undefined"}
                    {typeToString(type)}
                {/if}
            </span>
        </span>

        {#if mandatory}
            <span class="text-red-600 dark:text-red-500 font-medium text-xs lg:text-sm">required</span>
        {:else if mandatory !== undefined}
            <span class="font-normal text-muted-foreground text-xs lg:text-sm">optional</span>
        {/if}
    </div>
    <div class="text-xs lg:text-sm">
        {description ?? "No description."}
    </div>
</div>
