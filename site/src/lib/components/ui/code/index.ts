import { tv, type VariantProps } from 'tailwind-variants';
import Root from './code.svelte';
import Tabs from './code-tabs.svelte';
import Tab from './code-tab.svelte';
import Example from './code-example.svelte';
import Block from './code-block.svelte';

function keywordsToPattern(words: string) {
	return '\\b(?:' + words.trim().replace(/ /g, '|') + ')\\b';
}

export const codeVariants = tv({
	base: "bg-background border rounded-lg text-sm font-mono py-4 px-6",
	variants: {
		variant: {
			gsc: "",
			csc: "",
		},
	},
	defaultVariants: {
		variant: "gsc",
	},
});

type Variant = VariantProps<typeof codeVariants>["variant"];

export {
    Root,
    Tabs,
    Tab,
    Example,
    Block,
    type Variant
}