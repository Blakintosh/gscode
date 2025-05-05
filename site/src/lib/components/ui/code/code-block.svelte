<script lang="ts">
	import { cn } from "$lib/utils";
    import highlighterPromise from "$lib/util/syntax/gsc";
	import { codeVariants, type Variant } from ".";
	import { createHighlighter, type DecorationItem, type ShikiTransformer } from "shiki";
	import { transformerNotationDiff } from "@shikijs/transformers";
	import shakuCodeAnnotateShikiTransformer from "shaku-code-annotate-shiki-transformer";

    let className: string | null | undefined = undefined;

	export let variant: Variant = 'gsc';
    export let code: string;
    export let decorations: DecorationItem[] = [];
    export let transformers: ShikiTransformer[] = [
        transformerNotationDiff(),
        // doesn't let us transform because it doesn't recognise GSC - but it could just be a copy of JS otherwise
        shakuCodeAnnotateShikiTransformer()
    ];
	export { className as class };
</script>

{#await highlighterPromise then highlighter}
    {@html highlighter.codeToHtml(code, { 
        lang: "gsc", 
        themes: {
            light: "light-plus",
            dark: "dark-plus"
        },
        decorations, transformers
    })}
{/await}

<style lang="postcss">
    :global(.shiki) {
        @apply border rounded-lg text-sm font-mono py-4 px-6;
        background-color: var(--shiki-light);
    }

    :global(.shiki code) {
        @apply max-w-full break-words whitespace-break-spaces;
    }

    :global(.shiki code .line) {
        @apply block w-full;
    }

    :global(html.dark .shiki,
    html.dark .shiki .line span) {
        color: var(--shiki-dark) !important;
        /* Optional, if you also want font styles */
        font-style: var(--shiki-dark-font-style) !important;
        font-weight: var(--shiki-dark-font-weight) !important;
        text-decoration: var(--shiki-dark-text-decoration) !important;
    }

    :global(html.dark .shiki) {
        background-color: var(--shiki-dark-bg) !important;
    }

    :global(.shaku-underline) {
        padding: 0 1ch;
        position: relative;
        display: block;
        border-radius: 3px;
        color: var(--color-shaku-underline-light, red);
        margin: 0;
        width: min-content;
    }
    :global(.shaku-underline-line) {
        line-height: 0;
        top: 0.5em;
        position: absolute;
        text-decoration-line: overline;
        text-decoration-color: var(--color-shaku-underline-light, red);
        color: transparent;
        pointer-events: none;
        user-select: none;
        text-decoration-thickness: 2px;
    }
    :global(.shaku-underline-wavy > .shaku-underline-line) {
        text-decoration-style: wavy;
        top: 0.7em;
    }
    :global(.shaku-underline-solid > .shaku-underline-line) {
        text-decoration-color: var(--color-shaku-underline-light, orange);
        text-decoration-style: solid;
    }
    :global(.shaku-underline-dotted > .shaku-underline-line) {
        text-decoration-style: dotted;
    }
    :global(.shaku-inline-highlight) {
        @apply bg-sky-950 border-b-2 border-sky-500;
        margin: 0 1px;
        border-radius: 3px;
        padding: 0 3px;
    }
    :global(.shaku-inline-highlight[data-id="g"]) { 
        @apply bg-green-950 border-b-2 border-green-500;
    }
</style>