<script lang="ts">
	import BookOpen from '@lucide/svelte/icons/book-open';
	import Bug from '@lucide/svelte/icons/bug';
	import Download from '@lucide/svelte/icons/download';
	import FolderSearch from '@lucide/svelte/icons/folder-search';
	import Navigation from '@lucide/svelte/icons/navigation';
	import Palette from '@lucide/svelte/icons/palette';
	import Sparkles from '@lucide/svelte/icons/sparkles';
	import * as Code from '$lib/components/ui/code';
	import { Button } from '$lib/components/ui/button';
	import Brush from '$lib/components/site/Brush.svelte';
	import Eyebrow from '$lib/components/site/Eyebrow.svelte';
	import HudStat from '$lib/components/site/HudStat.svelte';
	import HudStrip from '$lib/components/site/HudStrip.svelte';
	import { extensionVersion, marketplaceUrl, siteUrl } from '$lib/data/site';

	const title = 'GSCode — the reference everything else is measured from';
	const description =
		"A language server for Black Ops III's GSC and CSC. It resolves your whole mod folder, then tells you what the Linker would reject — before you build.";

	const diagnostics = [
		{ line: 'Ln 1', message: "Unable to locate file 'scripts\\shared\\shrd.gsh' for insert directive." },
		{ line: 'Ln 5', message: "The operator '*' is not supported on types 'int' and 'string'." },
		{ line: 'Ln 11', message: "';' expected to end return statement." }
	];

	const completions = [
		{ name: 'damage_notify_wrapper', signature: 'function(damage, attacker, …)' },
		{ name: 'death_notify_wrapper', signature: 'function(attacker, damageType)' },
		{ name: 'debug_line', signature: 'function(start, end, …)' }
	];

	const cards = [
		{
			icon: Navigation,
			title: 'Code navigation',
			body: 'Jump to any definition, find every reference, and browse symbols across your entire workspace — with full namespace-qualified lookup.'
		},
		{
			icon: FolderSearch,
			title: 'Workspace indexing',
			body: 'Index your project for cross-file completions and diagnostics. Choose partial (fast, signatures only) or full semantic analysis.'
		},
		{
			icon: Palette,
			title: 'Semantic highlighting',
			body: 'Colour that distinguishes functions, variables, parameters, namespaces, classes, properties and macros — from the parse, not a regex.'
		}
	];

	const diagnosticsSample = `#insert scripts\\shared\\shrd.gsh;
//      ~~~~~~~~~~~~~~~~~~~~~~~~
function write_some_code( weapon_name )
{
    w_weapon = GetWeapon(weapon_name);
    ammo = w_weapon.clipsize * "2";
//                           ~~~~~
    current_health = self.health;

    if(current_health > 20)
    {
        self.health = current_health * 0.8;
    }

    return ammo
//             ~
}`;

	const completionsSample = `#using scripts\\shared\\util_shared;

function init()
{
    util::d
}`;
</script>

<svelte:head>
	<title>{title}</title>
	<meta name="description" content={description} />
	<meta property="og:type" content="website" />
	<meta property="og:site_name" content="gscode" />
	<meta property="og:title" content={title} />
	<meta property="og:description" content={description} />
	<meta property="og:image" content="{siteUrl}/og.png" />
	<meta name="twitter:card" content="summary_large_image" />
	<meta name="twitter:image" content="{siteUrl}/og.png" />
</svelte:head>

<!-- Hero: the one lit-grid surface on the site. Light comes from the top right, the
     datum origin sits top left, and one phrase carries the text gradient. -->
<section class="mx-auto w-full max-w-7xl px-4 pt-10 pb-6 sm:px-6 sm:pt-14">
	<Brush
		surface="popover"
		behind="background"
		handles
		rim="active"
		tab="vs code · language server"
		readout="gscode.net"
		shadow="panel"
		bodyClass="flex flex-col"
	>
		<div class="lit-grid relative overflow-hidden px-5 py-16 sm:px-9 sm:py-20 lg:px-12 lg:py-24">
			<div class="lit-grid-glow pointer-events-none absolute inset-0" aria-hidden="true"></div>
			<div
				class="pointer-events-none absolute inset-0 [background:radial-gradient(44%_52%_at_78%_-6%,color-mix(in_oklab,var(--bright)_20%,transparent),color-mix(in_oklab,var(--violet)_9%,transparent)_52%,transparent_74%)]"
				aria-hidden="true"
			></div>
			<div
				class="pointer-events-none absolute inset-y-0 left-[66%] w-[130px] skew-x-[-13deg] [background:linear-gradient(90deg,transparent,color-mix(in_oklab,var(--bright)_4%,transparent)_44%,color-mix(in_oklab,var(--bright)_24%,transparent)_50%,color-mix(in_oklab,var(--violet)_8%,transparent)_58%,transparent)] [mask-image:linear-gradient(#000,transparent_78%)]"
				aria-hidden="true"
			></div>

			<!-- Origin marker: everything on the page is measured from here. -->
			<div class="pointer-events-none absolute top-8 left-5 z-[2] sm:top-9 sm:left-9" aria-hidden="true">
				<i class="bg-primary absolute block h-px w-8"></i>
				<i class="bg-primary absolute block h-8 w-px"></i>
				<span
					class="text-primary absolute top-[-6px] left-9 font-mono text-[9px] tracking-[.12em] whitespace-nowrap"
				>
					0, 0
				</span>
			</div>

			<h1
				class="font-display relative z-[2] mt-6 max-w-[15ch] text-[clamp(30px,5.2vw,62px)] leading-none font-bold tracking-[.005em] uppercase sm:mt-0"
			>
				The reference<br /><span class="grad-text">everything else</span><br />is measured from
			</h1>
			<p class="text-muted-foreground relative z-[2] mt-6 max-w-[48ch] text-[17px]">
				{description}
			</p>
			<div class="relative z-[2] mt-8 flex flex-wrap gap-3">
				<Button href={marketplaceUrl} target="_blank" rel="noopener noreferrer" size="lg">
					<Download class="size-4" />
					Install for VS Code
				</Button>
				<Button href="/library" variant="secondary" size="lg">Function library</Button>
			</div>
			<p class="text-dim relative z-[2] mt-4 font-mono text-[10px] tracking-[.13em] uppercase">
				Available for VS Code and VS Code-based IDEs
			</p>
		</div>

		<HudStrip class="bg-card border-border border-t">
			<HudStat label="Languages" value="GSC · CSC" />
			<HudStat label="Version" value="v{extensionVersion}" />
			<HudStat label="Runtime" value=".NET 10" />
			<HudStat label="Licence" value="GPL-3.0" />
		</HudStrip>
	</Brush>
</section>

<div class="mx-auto w-full max-w-7xl space-y-8 px-4 pt-6 pb-16 sm:px-6 sm:pb-20">
	<!-- Story 1 · Diagnostics -->
	<Brush
		as="section"
		aria-label="Diagnostics"
		surface="card"
		behind="background"
		handles
		tab="Diagnostics"
		readout="3 err"
		bodyClass="grid items-start gap-8 p-5 sm:p-7 lg:grid-cols-12 lg:gap-10"
	>
		<div class="min-w-0 lg:col-span-5">
			<Eyebrow class="flex items-center gap-2">
				<Bug class="text-muted-foreground size-4" />
				Diagnostics
			</Eyebrow>
			<h2 class="mt-3.5 text-[clamp(21px,2.4vw,27px)] leading-[1.14] font-semibold tracking-[-.03em]">
				Catch what the Linker would reject
			</h2>
			<p class="text-muted-foreground mt-3.5">
				GSCode analyses your scripts as you type — syntax errors, missing references, type
				mismatches, unused variables and more, resolved across the whole mod folder.
			</p>
			<ul class="mt-6 space-y-2">
				{#each diagnostics as diagnostic (diagnostic.line)}
					<li
						class="bg-recess border-destructive border-l-2 px-3.5 py-2.5 font-mono text-[12.5px] leading-[1.5]"
					>
						<span class="text-dim">{diagnostic.line}</span>
						<span class="text-foreground">{diagnostic.message}</span>
					</li>
				{/each}
			</ul>
		</div>
		<div class="min-w-0 lg:col-span-7">
			<Code.Root value="1" language="GSC" behind="card" class="my-0">
				<Code.Tabs>
					<Code.Tab value="1">_weapon_utils.gsc</Code.Tab>
				</Code.Tabs>
				<Code.Example value="1">
					<Code.Block code={diagnosticsSample} />
				</Code.Example>
			</Code.Root>
		</div>
	</Brush>

	<!-- Story 2 · Hover documentation -->
	<Brush
		as="section"
		aria-label="Documentation"
		surface="card"
		behind="background"
		handles
		tab="Documentation"
		readout="1 sym"
		bodyClass="grid items-start gap-8 p-5 sm:p-7 lg:grid-cols-12 lg:gap-10"
	>
		<div class="min-w-0 lg:col-span-5 lg:order-last">
			<Eyebrow class="flex items-center gap-2">
				<BookOpen class="text-muted-foreground size-4" />
				Documentation
			</Eyebrow>
			<h2 class="mt-3.5 text-[clamp(21px,2.4vw,27px)] leading-[1.14] font-semibold tracking-[-.03em]">
				See what a symbol actually does
			</h2>
			<p class="text-muted-foreground mt-3.5">
				Hover any function, variable or property for its type, parameters and description —
				including a community-led database of documentation for the built-in API.
			</p>
		</div>
		<div class="min-w-0 lg:col-span-7">
			<Code.Root value="1" language="GSC" behind="card" class="my-0">
				<Code.Tabs>
					<Code.Tab value="1">_weapon_utils.gsc</Code.Tab>
				</Code.Tabs>
				<Code.Example value="1">
					<Code.Block code={`w_weapon = GetWeapon(weapon_name);`} />
				</Code.Example>
			</Code.Root>

			<!-- The hover card is a raise overlay with a real shadow, one step above the panel. -->
			<Brush
				surface="popover"
				behind="card"
				cut={10}
				shadow="overlay"
				class="mt-3 max-w-[440px] sm:ml-10"
				bodyClass="p-4"
			>
				<p class="font-mono text-[13px] break-words">
					<span class="text-foreground">GetWeapon</span><span class="text-muted-foreground"
						>(weaponName, attachmentName1, attachmentName2, …)</span
					>
				</p>
				<hr class="border-border my-3 border-t" />
				<p class="text-muted-foreground text-sm">
					Get the requested weapon object based on the game-mode-agnostic weapon name string.
				</p>
				<p class="type-label text-dim mt-4 tracking-[.19em]">Parameters</p>
				<dl class="mt-2.5 space-y-1.5 text-sm">
					<div class="flex flex-wrap items-baseline gap-x-3">
						<dt class="text-foreground font-mono text-[12.5px]">weaponName</dt>
						<dd class="text-muted-foreground text-[13px]">The base weapon to return.</dd>
					</div>
					<div class="flex flex-wrap items-baseline gap-x-3">
						<dt class="text-foreground font-mono text-[12.5px]">attachmentName1</dt>
						<dd class="text-muted-foreground text-[13px]">The first attachment name.</dd>
					</div>
				</dl>
			</Brush>

			<Brush
				surface="popover"
				behind="card"
				cut={7}
				shadow="overlay"
				class="mt-3 max-w-xs sm:ml-20"
				bodyClass="flex items-center gap-2.5 px-3.5 py-2.5 font-mono text-[12.5px]"
			>
				<i aria-hidden="true" class="bg-primary block size-[6px] shrink-0"></i>
				<span class="text-primary">/@ weapon @/</span>
				<span class="text-foreground">w_weapon</span>
			</Brush>
		</div>
	</Brush>

	<!-- Story 3 · Completions -->
	<Brush
		as="section"
		aria-label="Completions"
		surface="card"
		behind="background"
		handles
		tab="Completions"
		readout="4 items"
		bodyClass="grid items-start gap-8 p-5 sm:p-7 lg:grid-cols-12 lg:gap-10"
	>
		<div class="min-w-0 lg:col-span-5">
			<Eyebrow class="flex items-center gap-2">
				<Sparkles class="text-muted-foreground size-4" />
				Completions
			</Eyebrow>
			<h2 class="mt-3.5 text-[clamp(21px,2.4vw,27px)] leading-[1.14] font-semibold tracking-[-.03em]">
				Completions that read the namespace
			</h2>
			<p class="text-muted-foreground mt-3.5">
				Context-aware suggestions for functions, variables, keywords, macros and file paths — with
				full signature information and namespace resolution behind them.
			</p>
		</div>
		<div class="min-w-0 lg:col-span-7">
			<Code.Root value="1" language="GSC" behind="card" class="my-0">
				<Code.Tabs>
					<Code.Tab value="1">_init.gsc</Code.Tab>
				</Code.Tabs>
				<Code.Example value="1">
					<Code.Block code={completionsSample} />
				</Code.Example>
			</Code.Root>

			<!-- The completion list is a raise overlay; selection is a 2px left border + teal wash. -->
			<Brush
				surface="popover"
				behind="card"
				cut={10}
				shadow="overlay"
				class="mt-3 max-w-[520px] sm:ml-10"
				bodyClass="py-1.5"
			>
				{#each completions as item, index (item.name)}
					<div
						class="flex items-center gap-3 border-l-2 px-3 py-1.5 {index === 0
							? 'border-primary [background:var(--wash-active)]'
							: 'border-transparent'}"
					>
						<span
							class="chip-cut bg-primary text-primary-foreground grid size-[17px] shrink-0 place-items-center font-mono text-[10px] leading-none"
						>
							f
						</span>
						<span class="text-foreground truncate font-mono text-[12.5px]">{item.name}</span>
						<span class="text-dim ml-auto hidden font-mono text-[11px] sm:inline">
							{item.signature}
						</span>
					</div>
				{/each}
				<div class="text-dim flex items-center gap-3 border-l-2 border-transparent px-3 py-1.5">
					<i aria-hidden="true" class="bg-steel ml-[5px] block size-[6px] shrink-0"></i>
					<span class="font-mono text-[12.5px]">…</span>
				</div>
			</Brush>
		</div>
	</Brush>

	<!-- Card grid sits on the bare ground, never on a panel. -->
	<div class="grid grid-cols-1 gap-6 pt-8 md:grid-cols-3">
		{#each cards as card (card.title)}
			{@const Icon = card.icon}
			<Brush surface="card" behind="background" cut={12} bodyClass="p-4 sm:p-[17px]">
				<Icon class="text-muted-foreground size-5" />
				<h3 class="mt-3.5 text-[17px] font-semibold tracking-[-.03em]">{card.title}</h3>
				<p class="text-muted-foreground mt-2 text-sm">{card.body}</p>
			</Brush>
		{/each}
	</div>
</div>
