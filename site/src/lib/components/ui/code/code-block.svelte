<script lang="ts">
	import highlighterPromise from '$lib/util/syntax/gsc';
	import type { DecorationItem, ShikiTransformer } from 'shiki';
	import { transformerNotationDiff } from '@shikijs/transformers';
	import { shakuGscTransformer } from '$lib/util/shaku-gsc';

	let {
		code,
		decorations = [],
		transformers = [transformerNotationDiff(), shakuGscTransformer()]
	}: { code: string; decorations?: DecorationItem[]; transformers?: ShikiTransformer[] } =
		$props();
</script>

{#await highlighterPromise then highlighter}
	{@html highlighter.codeToHtml(code, {
		lang: 'gsc',
		themes: { light: 'datum-light', dark: 'datum-dark' },
		defaultColor: false,
		decorations,
		transformers
	})}
{/await}

<!-- Shiki emits inline --shiki-light/--shiki-dark vars; the panel supplies the surface. -->
<style>
	:global(.shiki) {
		background: transparent !important;
		font-family: var(--font-mono);
		font-size: 13px;
		line-height: 1.6;
		padding: 16px 20px;
		margin: 0;
	}
	:global(.shiki),
	:global(.shiki span) {
		color: var(--shiki-light);
	}
	:global(html.dark .shiki),
	:global(html.dark .shiki span) {
		color: var(--shiki-dark);
		font-style: var(--shiki-dark-font-style);
		font-weight: var(--shiki-dark-font-weight);
	}
	:global(.shiki code) {
		display: block;
		max-width: 100%;
		white-space: pre;
	}
	:global(.shiki code .line) {
		display: block;
		width: 100%;
	}

	/* Shaku annotations — error underlines are Clip (the destructive colour), the one
	   warm-leaning value in the system, reserved for exactly this meaning. */
	:global(.shiki .shaku-underline) {
		padding: 0 1ch;
		position: relative;
		display: block;
		color: var(--destructive) !important;
		margin: 0;
		width: min-content;
	}
	:global(.shiki .shaku-underline-line) {
		line-height: 0;
		top: 0.5em;
		position: absolute;
		text-decoration-line: overline;
		text-decoration-color: var(--destructive);
		color: transparent !important;
		pointer-events: none;
		user-select: none;
		text-decoration-thickness: 2px;
	}
	:global(.shaku-underline-wavy > .shaku-underline-line) {
		text-decoration-style: wavy;
		top: 0.7em;
	}
	:global(.shaku-underline-solid > .shaku-underline-line) {
		text-decoration-color: var(--primary);
		text-decoration-style: solid;
	}
	:global(.shaku-underline-dotted > .shaku-underline-line) {
		text-decoration-style: dotted;
	}
	:global(.shaku-inline-highlight) {
		background: var(--wash-active);
		border-bottom: 2px solid var(--primary);
		margin: 0 1px;
		padding: 0 3px;
	}
</style>
