<script lang="ts">
	import ArrowUpRightIcon from '@lucide/svelte/icons/arrow-up-right';
	import DownloadIcon from '@lucide/svelte/icons/download';
	import { Button } from '$lib/components/ui/button';
	import HudStat from '$lib/components/site/HudStat.svelte';
	import HudStrip from '$lib/components/site/HudStrip.svelte';
	import DiscordIcon from '$lib/components/site/DiscordIcon.svelte';
	import DiagnosticsWidget from '$lib/components/home/DiagnosticsWidget.svelte';
	import HoverWidget from '$lib/components/home/HoverWidget.svelte';
	import CompletionsWidget from '$lib/components/home/CompletionsWidget.svelte';
	import GamesWidget from '$lib/components/home/GamesWidget.svelte';
	import { reveal } from '$lib/actions/reveal';
	import {
		assetplaceUrl,
		discordInviteUrl,
		extensionVersion,
		marketplaceUrl,
		siteUrl,
		wikiUrl
	} from '$lib/data/site';

	// Each spread's widget starts its sequence when the spread scrolls in.
	let live = $state({ diagnostics: false, hover: false, completions: false, games: false });

	const title = 'GSCode — the reference everything else is measured from';
	const description =
		"A language server for Call of Duty's GSC and CSC: diagnostics, completions, navigation and a function library, before you build.";

	const spec = [
		['Diagnostics', 'Syntax, references, types, unused symbols', 'resolved across the whole mod folder, as you type'],
		['Navigation', 'Definition · references · rename', 'call and type hierarchy, workspace symbols, document links'],
		['Inference', 'Type-flow inlay hints', 'seeded with the engine’s object-field types'],
		['Formatting', 'Whitespace-only', 'refuses syntax errors and re-checks its own output'],
		['Code actions', 'Add / remove #using', 'backed by a namespace-usage lint'],
		['Mod tools', 'share/raw + mods/<name>', 'indexed in isolation with overlay resolution; workspace-only mode too'],
		['Cache', 'Workspace cache', 'Pick up quickly from where you left off after a restart.'],
		['Suppression', '#pragma warning disable', 'Suppress diagnostics per line, per code, or all.'],
		['Open in Library', 'shift + F1', 'Shortcut to open the API library page for any built-in function.']
	] as const;
</script>

<svelte:head>
	<title>{title}</title>
	<meta name="description" content={description} />
	<meta property="og:title" content="GSCode" />
	<meta property="og:site_name" content="gscode" />
	<meta property="og:description" content={description} />
	<meta property="og:image" content="{siteUrl}/og.png" />
	<meta property="og:url" content={siteUrl} />
	<meta name="twitter:card" content="summary_large_image" />
</svelte:head>

<!-- ── Hero: the whole viewport is the frame. One light source, top-right. ─────────── -->
<section class="bg-popover relative overflow-hidden" aria-labelledby="hero-title">
	<div class="lit-grid pointer-events-none absolute inset-0" aria-hidden="true"></div>
	<div class="lit-grid-glow pointer-events-none absolute inset-0" aria-hidden="true"></div>
	<div
		class="pointer-events-none absolute inset-0"
		aria-hidden="true"
		style="background:radial-gradient(44% 52% at 78% -6%, color-mix(in oklab, var(--bright) 20%, transparent), color-mix(in oklab, var(--violet) 9%, transparent) 52%, transparent 74%)"
	></div>
	<!-- The light entering: sweeps in once on load, then rests. -->
	<div
		class="light-enter pointer-events-none absolute top-0 bottom-0 left-[62%] w-[140px] [transform:skewX(-13deg)]"
		aria-hidden="true"
		style="background:linear-gradient(90deg, transparent, color-mix(in oklab, var(--bright) 5%, transparent) 44%, color-mix(in oklab, var(--bright) 17%, transparent) 50%, color-mix(in oklab, var(--violet) 8%, transparent) 58%, transparent);mask-image:linear-gradient(180deg, #000 0, #000 55%, transparent 100%)"
	></div>

	<!-- The frame declares itself: tag in the top-left slot, handles on the square corners. -->
	<span
		class="tab-cut bg-primary text-primary-foreground absolute top-0 left-0 z-10 px-[13px] py-1 pl-[9px] font-mono text-2xs leading-none tracking-label uppercase"
		>vs code · language server</span
	>
	<i aria-hidden="true" class="border-primary bg-background absolute top-3 right-3 z-10 block size-[7px] border-[1.5px]"></i>
	<i aria-hidden="true" class="border-steel bg-background absolute bottom-3 left-3 z-10 block size-[7px] border-[1.5px]"></i>

	<div class="relative mx-auto max-w-7xl px-4 pt-20 pb-24 sm:px-6 lg:pt-28 lg:pb-32">
		<!-- Origin crosshair -->
		<div class="text-primary pointer-events-none absolute top-8 left-4 font-mono text-2xs tracking-widest sm:left-6" aria-hidden="true">
			<i class="bg-primary absolute block h-px w-8"></i>
			<i class="bg-primary absolute block h-8 w-px"></i>
			<span class="absolute top-[-6px] left-9 whitespace-nowrap">0, 0</span>
		</div>

		<h1
			id="hero-title"
			class="font-display text-foreground max-w-[16ch] text-hero font-bold tracking-normal uppercase"
		>
			IDE tooling for<br />
			Call of Duty
			<span class="grad-text">scripting</span><br />
		</h1>
		<p class="text-muted-foreground mt-7 max-w-[52ch] text-lg leading-relaxed font-light sm:text-xl">
			A language server for Call of Duty’s GSC and CSC — Black Ops III first, and every game
			back to Call of Duty 4. It resolves your whole mod folder, then tells you what the Linker
			would reject, before you build.
		</p>
		<div class="mt-9 flex flex-wrap items-center gap-3">
			<Button href={marketplaceUrl} target="_blank" rel="noopener noreferrer" size="lg">
				<DownloadIcon class="size-4" />
				Install for VS Code
			</Button>
			<Button href="/library" variant="secondary" size="lg">Function library</Button>
		</div>
		<p class="type-label text-dim mt-6">
			free · open source · vs code and vs code-based ides
		</p>
	</div>
</section>

<!-- HUD strip runs the full width: the instrument's readouts. -->
<div class="border-border border-y">
	<HudStrip class="[box-shadow:none] border-0">
		<HudStat label="Languages" value="GSC · CSC · GSH" />
		<HudStat label="Games" value="5" sub="CoD4 → Black Ops III" />
		<HudStat label="Version" value="v{extensionVersion}" />
		<HudStat label="Runtime" value=".NET 10" />
		<HudStat label="Licence" value="GPL-3.0" />
	</HudStrip>
</div>

<!-- ── Spreads: full-width, title block on one side, the live widget on the other. ─── -->
{#snippet titleBlock(no: string, name: string, readout: string, heading: string, copy: string)}
	<div class="reveal" use:reveal>
		<p class="type-label text-primary">
			{no} / {name} <span class="text-dim">· {readout}</span>
		</p>
		<h2 class="text-foreground mt-4 max-w-[16ch] text-heading font-semibold tracking-heading sm:text-heading">
			{heading}
		</h2>
		<p class="text-muted-foreground mt-5 max-w-[46ch] text-body leading-relaxed font-light">{copy}</p>
	</div>
{/snippet}

<section class="border-border border-b" use:reveal={{ onIn: () => (live.diagnostics = true), threshold: 0.25 }}>
	<div class="mx-auto grid max-w-7xl gap-10 px-4 py-20 sm:px-6 lg:grid-cols-12 lg:gap-14 lg:py-28">
		<div class="min-w-0 lg:col-span-4">
			{@render titleBlock(
				'01',
				'Diagnostics',
				'3 err',
				'Don\'t wait for Linker to tell you',
				'Syntax errors, missing files, mismatched types, unused variables — analysed as you type and resolved across the whole mod folder, not just the open file. Errors show up where they are, in the same words the compiler would use.'
			)}
		</div>
		<div class="min-w-0 lg:col-span-8"><DiagnosticsWidget active={live.diagnostics} /></div>
	</div>
</section>

<section class="border-border border-b" use:reveal={{ onIn: () => (live.hover = true), threshold: 0.25 }}>
	<div class="mx-auto grid max-w-7xl gap-10 px-4 py-20 sm:px-6 lg:grid-cols-12 lg:gap-14 lg:py-28">
		<div class="order-2 min-w-0 lg:order-1 lg:col-span-7"><HoverWidget active={live.hover} /></div>
		<div class="order-1 min-w-0 lg:order-2 lg:col-span-5">
			{@render titleBlock(
				'02',
				'Documentation',
				'1 sym',
				'See what a symbol actually does',
				'Hover any function, variable or property for its type, parameters and description — drawn from a community-maintained library of the built-in API, and from type-flow inference for your own locals.'
			)}
		</div>
	</div>
</section>

<section class="border-border border-b" use:reveal={{ onIn: () => (live.completions = true), threshold: 0.25 }}>
	<div class="mx-auto grid max-w-7xl gap-10 px-4 py-20 sm:px-6 lg:grid-cols-12 lg:gap-14 lg:py-28">
		<div class="min-w-0 lg:col-span-4">
			{@render titleBlock(
				'03',
				'Completions',
				'4 items',
				'Completions that read the namespace',
				'Functions, variables, keywords, macros and file paths, filtered by what you have typed and where you are — with signature help attached and namespaces resolved the way the engine resolves them.'
			)}
		</div>
		<div class="min-w-0 lg:col-span-8"><CompletionsWidget active={live.completions} /></div>
	</div>
</section>

<section class="border-border border-b" use:reveal={{ onIn: () => (live.games = true), threshold: 0.25 }}>
	<div class="mx-auto grid max-w-7xl gap-10 px-4 py-20 sm:px-6 lg:grid-cols-12 lg:gap-14 lg:py-28">
		<div class="min-w-0 lg:col-span-4">
			{@render titleBlock(
				'04',
				'Every game',
				'5 versions',
				'One extension. Five dialects.',
				'Developed first for Black Ops III; the extension now supports a range of GSC dialects. GSCode also includes support for Call of Duty 4, World at War, Modern Warfare 2 and Black Ops. Set the game once; the status bar shows what is active.'
			)}
		</div>
		<div class="min-w-0 lg:col-span-8"><GamesWidget active={live.games} /></div>
	</div>
</section>

<!-- ── Spec sheet: what else is in the instrument. ────────────────────────────────── -->
<section class="border-border border-b">
	<div class="mx-auto max-w-7xl px-4 py-20 sm:px-6 lg:py-28">
		<div class="reveal mb-10 flex flex-wrap items-end justify-between gap-4" use:reveal>
			<div>
				<p class="type-label text-primary">05 / Specification <span class="text-dim">· v{extensionVersion}</span></p>
				<h2 class="text-foreground mt-4 text-heading font-semibold tracking-heading sm:text-heading">
					Some more on features
				</h2>
			</div>
			<p class="text-muted-foreground max-w-[44ch] text-base leading-relaxed font-light">
				GSCode includes various IntelliSense features to make scripting easier and faster. The extension is free, open source and maintained by the community.
			</p>
		</div>
		<dl class="border-border reveal border-t" use:reveal>
			{#each spec as [label, value, note] (label)}
				<div class="border-border grid gap-x-8 gap-y-1 border-b py-4 sm:grid-cols-[160px_minmax(0,1fr)] lg:grid-cols-[200px_320px_minmax(0,1fr)]">
					<dt class="type-label text-dim self-center">{label}</dt>
					<dd class="text-foreground text-base font-medium">{value}</dd>
					<dd class="text-muted-foreground text-sm leading-normal font-light lg:self-center">{note}</dd>
				</div>
			{/each}
		</dl>
	</div>
</section>

<!-- ── Closing band: measure from here, and the rest of the ecosystem. ────────────── -->
<section class="bg-popover relative overflow-hidden">
	<div class="lit-grid pointer-events-none absolute inset-0 opacity-60" aria-hidden="true"></div>
	<div class="relative mx-auto grid max-w-7xl gap-12 px-4 py-20 sm:px-6 lg:grid-cols-12 lg:gap-14 lg:py-24">
		<div class="reveal lg:col-span-6" use:reveal>
			<h2 class="font-display text-foreground text-display font-bold tracking-normal uppercase">
				Try it for<br /><span class="grad-text">yourself</span>
			</h2>
			<p class="text-muted-foreground mt-5 max-w-[42ch] text-body leading-relaxed font-light">
				Install the extension, open your mod folder, and experience
				a streamlined scripting workflow today.
			</p>
			<div class="mt-7 flex flex-wrap items-center gap-3">
				<Button href={marketplaceUrl} target="_blank" rel="noopener noreferrer" size="lg">
					<DownloadIcon class="size-4" />
					Install
					<span class="type-data text-2xs tracking-wider">v{extensionVersion}</span>
				</Button>
				<Button href={discordInviteUrl} target="_blank" rel="noopener noreferrer" variant="secondary" size="lg">
					<DiscordIcon class="size-4" />
					Discord
				</Button>
			</div>
		</div>
		<div class="reveal grid gap-px sm:grid-cols-3 lg:col-span-6 [transition-delay:80ms]" use:reveal>
			{#each [{ href: wikiUrl, label: 'Wiki', copy: 'Guides and reference for BO3 modding — from first map to shipped mod.' }, { href: assetplaceUrl, label: 'Assetplace', copy: 'Community-built weapons, prefabs, scripts and tools, versioned and credited.' }, { href: discordInviteUrl, label: 'Discord', copy: 'The BO3 Mod Tools server: help, releases and the people behind them.' }] as item (item.href)}
				<a
					href={item.href}
					target="_blank"
					rel="noopener noreferrer"
					class="group bg-card inset-edge flex flex-col gap-3 p-5 transition-colors hover:bg-[var(--wash-hover)] focus-visible:shadow-[inset_0_0_0_1px_var(--ring)] outline-none"
				>
					<span class="type-label text-primary flex items-center justify-between">
						{item.label}
						<ArrowUpRightIcon class="text-dim group-hover:text-primary size-3.5 transition-colors" />
					</span>
					<span class="text-muted-foreground group-hover:text-foreground text-sm leading-normal font-light transition-colors">{item.copy}</span>
				</a>
			{/each}
		</div>
	</div>
</section>
