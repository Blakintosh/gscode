<script module lang="ts">
	import type { Snippet } from "svelte";
	import type { ButtonProps } from "../button";
    import { Button } from "../button";

    export type CopyButtonProps = ButtonProps & {
        child: Snippet<[{ copied: boolean }]>;
        text: string;
    };
</script>

<script lang="ts">
    let { child, text, class: className, ...props }: CopyButtonProps = $props();
    let copied = $state(false);

    let copyStateTimeout: ReturnType<typeof setTimeout> | undefined = undefined;

    function copy() {
        navigator.clipboard.writeText(text);
        copied = true;

        if(copyStateTimeout) {
            clearTimeout(copyStateTimeout);
        }

        copyStateTimeout = setTimeout(() => {
            copied = false;
        }, 1500);
    }
</script>

<Button class={className} {...props} onclick={copy}>
    {@render child({ copied })}
</Button>