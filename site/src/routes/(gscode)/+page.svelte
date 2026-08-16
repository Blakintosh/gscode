<script>
	import { onMount } from 'svelte';
	import * as Code from '$lib/components/ui/code';
	import Download from '@lucide/svelte/icons/download';
	import Bug from '@lucide/svelte/icons/bug';
	import BookOpen from '@lucide/svelte/icons/book-open';
	import Sparkles from '@lucide/svelte/icons/sparkles';
	import Navigation from '@lucide/svelte/icons/navigation';
	import FolderSearch from '@lucide/svelte/icons/folder-search';
	import Palette from '@lucide/svelte/icons/palette';
</script>

<svelte:head>
	<title>GSCode</title>
	<meta name="description" content="A better way to write scripts for Call of Duty" />
	<meta property="og:title" content="GSCode" />
	<meta property="og:site_name" content="gscode" />
	<meta property="og:description" content="A better way to write scripts for Call of Duty" />
	<meta property="og:image" content="/favicon.png" />
</svelte:head>

<section
	class="flex w-full items-center justify-center flex-col bg-[radial-gradient(ellipse_at_center,var(--background)_0%,var(--background)/80_50%,var(--background)_100%)]"
>
	<div class="flex items-center flex-col pt-8 pb-4 text-foreground">
		<div class="my-4">
			<!-- <img src="/images/gscode.png" alt="GSCode Logo" class="w-64 md:w-72 lg:w-96"/> -->
			<div class="w-56 md:w-64 lg:w-80 aspect-[2/1] bg-gscodeLight dark:bg-gscode bg-cover"></div>
		</div>
		<div class="inline-flex gap-1 mb-2">
			<h2 id="gscode-summary" class="text-base lg:text-2xl">
				A better way to write scripts for Call of Duty.
			</h2>
		</div>
		<h3 class="text-muted-foreground lg:text-xl my-2 inline-flex items-center gap-1">
			Available for
			<img src="/images/code-stable.png" alt="Visual Studio Code" class="h-4 lg:h-6 inline mx-2" />
			VS Code based IDEs
		</h3>

		<a
			href="https://marketplace.visualstudio.com/items?itemName=blakintosh.gscode"
			class="preview-button my-8 text-white lg:text-xl text-base"
			target="_blank"
			rel="noopener noreferrer"
		>
			<Download class="w-5 h-5" />
			<span>v1.5.0</span>
		</a>
	</div>
</section>

<section class="flex w-full justify-center bg-background">
	<div class="max-w-6xl w-full px-6 lg:px-16 py-16 lg:py-24 flex flex-col gap-20 lg:gap-28">
		<div class="text-center space-y-3">
			<h2 class="text-3xl lg:text-4xl font-display">Script more intelligently</h2>
			<p class="text-muted-foreground lg:text-lg max-w-2xl mx-auto">
				A full-featured language server that understands your GSC and CSC scripts.
			</p>
		</div>

		<!-- Feature 1: Real-Time Diagnostics -->
		<div class="flex flex-col lg:flex-row gap-8 lg:gap-12 items-start">
			<div class="lg:w-5/12 space-y-4">
				<div class="inline-flex items-center gap-2 text-red-500">
					<Bug class="w-5 h-5" />
					<span class="text-sm font-medium uppercase tracking-wide">Diagnostics</span>
				</div>
				<h3 class="text-2xl lg:text-3xl font-display">Catch errors before you compile</h3>
				<p class="text-muted-foreground lg:text-lg">
					GSCode analyses your scripts as you type — catching syntax errors, missing references, type mismatches, unused variables, and more.
				</p>
				<div class="space-y-2 pt-2">
					<div class="border-l-4 border-l-red-500 bg-red-500/5 rounded-r-md px-4 py-2 text-sm font-mono">
						<span class="text-muted-foreground">Ln 1:</span> Unable to locate file 'scripts\shared\shrd.gsh' for insert directive.
					</div>
					<div class="border-l-4 border-l-red-500 bg-red-500/5 rounded-r-md px-4 py-2 text-sm font-mono">
						<span class="text-muted-foreground">Ln 5:</span> The operator '*' is not supported on types 'int' and 'string'.
					</div>
					<div class="border-l-4 border-l-red-500 bg-red-500/5 rounded-r-md px-4 py-2 text-sm font-mono">
						<span class="text-muted-foreground">Ln 11:</span> ';' expected to end return statement.
					</div>
				</div>
			</div>
			<div class="lg:w-7/12 w-full">
				<Code.Root value={"1"}>
					<Code.Tabs>
						<Code.Tab value={"1"}>_weapon_utils.gsc</Code.Tab>
					</Code.Tabs>
					<Code.Example value={"1"}>
						<Code.Block code={
`#insert scripts\\shared\\shrd.gsh;
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
}`
						}/>
					</Code.Example>
				</Code.Root>
			</div>
		</div>

		<!-- Feature 2: Hover Documentation -->
		<div class="flex flex-col lg:flex-row-reverse gap-8 lg:gap-12 items-start">
			<div class="lg:w-5/12 space-y-4">
				<div class="inline-flex items-center gap-2 text-sky-500">
					<BookOpen class="w-5 h-5" />
					<span class="text-sm font-medium uppercase tracking-wide">Documentation</span>
				</div>
				<h3 class="text-2xl lg:text-3xl font-display">See what your code does</h3>
				<p class="text-muted-foreground lg:text-lg">
					Hover over any function, variable, or property to see its type, parameters, and description — including documentation from a community-led database of built-in functions.
				</p>
			</div>
			<div class="lg:w-7/12 w-full space-y-4">
				<Code.Root value={"1"}>
					<Code.Tabs>
						<Code.Tab value={"1"}>_weapon_utils.gsc</Code.Tab>
					</Code.Tabs>
					<Code.Example value={"1"}>
						<Code.Block code={`w_weapon = GetWeapon(weapon_name);`}/>
					</Code.Example>
				</Code.Root>
				<div class="hover-tooltip">
					<div class="font-mono text-sm">
						<span class="text-foreground">GetWeapon</span><span class="text-muted-foreground">(weaponName, attachmentName1, attachmentName2, ...)</span>
					</div>
					<hr class="border-t my-2" />
					<p class="text-sm text-muted-foreground">Get the requested weapon object based on game mode agnostic weapon name string.</p>
					<div class="mt-2 text-sm">
						<span class="text-muted-foreground">Parameters:</span>
						<div class="ml-2 mt-1 space-y-1">
							<div>
								<span class="font-mono text-foreground">weaponName</span>
								<span class="text-muted-foreground ml-2">The name of the base weapon to return.</span>
							</div>
							<div>
								<span class="font-mono text-foreground">attachmentName1</span>
								<span class="text-muted-foreground ml-2">The first attachment name for the weapon.</span>
							</div>
						</div>
					</div>
				</div>
				<div class="hover-tooltip max-w-xs">
					<div class="font-mono text-sm">
						<span class="text-sky-400">/@ weapon @/</span>
						<span class="text-foreground ml-1">w_weapon</span>
					</div>
				</div>
			</div>
		</div>

		<!-- Feature 3: Intelligent Completions -->
		<div class="flex flex-col lg:flex-row gap-8 lg:gap-12 items-start">
			<div class="lg:w-5/12 space-y-4">
				<div class="inline-flex items-center gap-2 text-violet-500">
					<Sparkles class="w-5 h-5" />
					<span class="text-sm font-medium uppercase tracking-wide">Completions</span>
				</div>
				<h3 class="text-2xl lg:text-3xl font-display">Completions that understand your code</h3>
				<p class="text-muted-foreground lg:text-lg">
					Context-aware suggestions for functions, variables, keywords, macros, and file paths — with full signature information and namespace support.
				</p>
			</div>
			<div class="lg:w-7/12 w-full space-y-0">
				<Code.Root value={"1"}>
					<Code.Tabs>
						<Code.Tab value={"1"}>_init.gsc</Code.Tab>
					</Code.Tabs>
					<Code.Example value={"1"}>
						<Code.Block code={
`#using scripts\\shared\\util_shared;

function init()
{
    util::d
}`
						}/>
					</Code.Example>
				</Code.Root>
				<div class="completion-dropdown">
					<div class="completion-item completion-item-selected">
						<span class="completion-icon">f</span>
						<span class="font-mono text-sm text-foreground">damage_notify_wrapper</span>
						<span class="text-xs text-muted-foreground ml-auto">function(damage, attacker, ...)</span>
					</div>
					<div class="completion-item">
						<span class="completion-icon">f</span>
						<span class="font-mono text-sm text-foreground">death_notify_wrapper</span>
						<span class="text-xs text-muted-foreground ml-auto">function(attacker, damageType)</span>
					</div>
					<div class="completion-item">
						<span class="completion-icon">f</span>
						<span class="font-mono text-sm text-foreground">debug_line</span>
						<span class="text-xs text-muted-foreground ml-auto">function(start, end, ...)</span>
					</div>
					<div class="completion-item">
						<span class="completion-icon">...</span>
						<span class="font-mono text-sm text-foreground">...</span>
						<span class="text-xs text-muted-foreground ml-auto">...</span>
					</div>
				</div>
			</div>
		</div>

		<!-- Feature Cards -->
		<div class="grid grid-cols-1 md:grid-cols-3 gap-6">
			<div class="border rounded-lg p-6 space-y-3 bg-background">
				<Navigation class="w-8 h-8 text-emerald-500" />
				<h4 class="text-lg font-display font-medium">Code Navigation</h4>
				<p class="text-sm text-muted-foreground">
					Jump to any definition, find every reference, and browse symbols across your entire workspace — with full namespace-qualified lookup support.
				</p>
			</div>
			<div class="border rounded-lg p-6 space-y-3 bg-background">
				<FolderSearch class="w-8 h-8 text-amber-500" />
				<h4 class="text-lg font-display font-medium">Workspace Indexing</h4>
				<p class="text-sm text-muted-foreground">
					Index your project for cross-file completions and diagnostics. Choose between partial (fast, signatures only) or full (complete semantic analysis) modes.
				</p>
			</div>
			<div class="border rounded-lg p-6 space-y-3 bg-background">
				<Palette class="w-8 h-8 text-pink-500" />
				<h4 class="text-lg font-display font-medium">Semantic Highlighting</h4>
				<p class="text-sm text-muted-foreground">
					Rich syntax coloring that distinguishes functions, variables, parameters, namespaces, classes, properties, macros, and more.
				</p>
			</div>
		</div>
	</div>
</section>

<style>
	.preview-button {
		display: inline-flex;
		gap: 1rem;
		align-items: center;
		padding: 12px 36px;
		border-radius: 24px;
		font-weight: 400;
		text-decoration: none;
		color: #fff;
		background: linear-gradient(45deg, #4813ba, #182af2);
		background-size: 200% 200%;
		animation: gradient 3s ease infinite;
		box-shadow: 0 4px 15px rgba(0, 0, 0, 0.2);
		transition: transform 0.2s;
	}

	.preview-button:hover {
		transform: translateY(-3px);
		box-shadow: 0 6px 20px rgba(0, 0, 0, 0.25);
	}

	@keyframes gradient {
		0% {
			background-position: 0% 50%;
		}
		50% {
			background-position: 100% 50%;
		}
		100% {
			background-position: 0% 50%;
		}
	}

	.hover-tooltip {
		@apply border rounded-lg bg-background shadow-lg p-4;
	}

	.completion-dropdown {
		@apply border rounded-b-lg bg-background shadow-lg py-1 -mt-1;
	}

	.completion-item {
		@apply px-3 py-1.5 flex items-center gap-3;
	}

	.completion-item-selected {
		@apply bg-accent;
	}

	.completion-icon {
		@apply w-5 h-5 rounded-sm bg-violet-500 flex items-center justify-center text-xs text-white font-bold;
	}
</style>
