<script lang="ts">
	/**
	 * Brush — the Datum chamfered surface. Every panel declares itself on all four corners:
	 * the top-left cut docks the type tab, the bottom-right cut carries a readout, the two
	 * square corners take 7px handles, and a 1px rim gradient runs around the body.
	 *
	 * Structure (clip-path clips borders, shadows and outlines, so each job lives on its
	 * own layer):
	 *   wrapper (relative, drop-shadow, handles, tab, readout)
	 *     └ rim (clipped, gradient background)
	 *         └ ::before body (inset 1px, clipped, surface colour)
	 *         └ content (z-1)
	 */
	import type { Snippet } from 'svelte';
	import type { HTMLAttributes, HTMLAnchorAttributes } from 'svelte/elements';
	import { cn } from '$lib/utils.js';

	type Rim = 'rest' | 'active' | 'danger' | 'deep' | 'edge' | 'none';
	type Surface = 'card' | 'popover' | 'background' | 'recess' | 'table';
	type Shadow = 'none' | 'panel' | 'overlay';

	let {
		as = 'div',
		href,
		cut = 15,
		rim = 'rest',
		surface = 'card',
		/** What the handles and readout paint over — the surface *behind* the brush. */
		behind = 'background',
		tab,
		tabClass = '',
		tabStyle = '',
		readout,
		readoutClass = '',
		handles = false,
		shadow = 'none',
		/** Hover turns the whole assembly bright: rim, tab, handles, readout. Press = 1px. */
		interactive = false,
		class: className = '',
		bodyClass = '',
		children,
		...rest
	}: {
		as?: 'div' | 'a' | 'section' | 'article' | 'li' | 'aside' | 'header' | 'form';
		href?: string;
		cut?: 15 | 12 | 10 | 8 | 7 | 6;
		rim?: Rim;
		surface?: Surface;
		behind?: Surface;
		tab?: string;
		tabClass?: string;
		tabStyle?: string;
		readout?: string;
		readoutClass?: string;
		handles?: boolean;
		shadow?: Shadow;
		interactive?: boolean;
		class?: string;
		bodyClass?: string;
		children?: Snippet;
	} & Omit<HTMLAttributes<HTMLElement>, 'class'> &
		Omit<HTMLAnchorAttributes, 'class' | 'href'> = $props();

	const surfaceVar: Record<Surface, string> = {
		card: 'var(--card)',
		popover: 'var(--popover)',
		background: 'var(--background)',
		recess: 'var(--recess)',
		table: 'var(--table)'
	};

	const rimClass: Record<Rim, string> = {
		rest: 'rim',
		active: 'rim-active',
		danger: 'rim-danger',
		deep: 'rim-deep',
		edge: 'rim-edge',
		none: ''
	};

	const shadowClass: Record<Shadow, string> = {
		none: '',
		panel: '[filter:drop-shadow(var(--shadow-panel))]',
		overlay: '[filter:drop-shadow(var(--shadow-overlay))]'
	};

	const handleColour = $derived(
		rim === 'active' ? 'var(--primary)' : rim === 'danger' ? 'var(--destructive)' : 'var(--handle)'
	);

	const tag = $derived(href ? 'a' : as);
</script>

<svelte:element
	this={tag}
	{href}
	data-slot="brush"
	data-rim={rim}
	class={cn(
		'group/brush relative block',
		shadowClass[shadow],
		interactive && 'cursor-pointer outline-none active:translate-y-px',
		className
	)}
	style="--brush-cut:{cut}px;--brush-surface:{surfaceVar[surface]};--brush-behind:{surfaceVar[
		behind
	]};--brush-handle:{handleColour}"
	{...rest}
>
	{#if handles}
		<i
			aria-hidden="true"
			class={cn(
				'pointer-events-none absolute -top-1 -right-1 z-[6] block size-[7px] border-[1.5px] transition-colors [border-color:var(--brush-handle)]',
				interactive && 'group-hover/brush:[border-color:var(--bright)]'
			)}
			style="background:var(--brush-behind)"
		></i>
		<i
			aria-hidden="true"
			class={cn(
				'pointer-events-none absolute -bottom-1 -left-1 z-[6] block size-[7px] border-[1.5px] transition-colors [border-color:var(--brush-handle)]',
				interactive && 'group-hover/brush:[border-color:var(--bright)]'
			)}
			style="background:var(--brush-behind)"
		></i>
	{/if}

	{#if tab}
		<span
			data-slot="brush-tab"
			class={cn(
				'tab-cut bg-primary text-primary-foreground pointer-events-none absolute top-0 left-0 z-[5] font-mono text-[11px] leading-none tracking-[.16em] uppercase transition-colors',
				'px-[13px] py-1 pl-[9px]',
				interactive && 'group-hover/brush:bg-bright group-hover/brush:text-ink',
				tabClass
			)}
			style={tabStyle}
		>
			{tab}
		</span>
	{/if}

	{#if readout}
		<span
			data-slot="brush-readout"
			class={cn(
				'pointer-events-none absolute right-0 -bottom-2 z-[5] font-mono text-[11px] leading-[16px] tracking-[.1em] transition-colors',
				rim === 'active' ? 'text-primary' : 'text-dim',
				interactive && 'group-hover/brush:text-bright',
				readoutClass
			)}
			style="background:var(--brush-behind);padding:0 7px"
		>
			{readout}
		</span>
	{/if}

	<div
		data-slot="brush-rim"
		class={cn(
			// p-px keeps the content inside the 1px rim (covers full-bleed media, e.g. card covers).
			'chamfer relative p-px transition-[background] duration-150 [--cut:var(--brush-cut)]',
			rimClass[rim],
			interactive && 'group-hover/brush:rim-hover group-focus-visible/brush:[background:var(--ring)]',
			// Body: 1px inset, same chamfer, surface colour.
			"before:pointer-events-none before:absolute before:inset-px before:z-0 before:content-[''] before:[clip-path:polygon(var(--cut)_0,100%_0,100%_calc(100%_-_var(--cut)),calc(100%_-_var(--cut))_100%,0_100%,0_var(--cut))] before:[background:var(--brush-surface)]"
		)}
	>
		<div data-slot="brush-body" class={cn('relative z-[1]', bodyClass)}>
			{@render children?.()}
		</div>
	</div>
</svelte:element>
