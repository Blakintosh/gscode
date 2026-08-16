<script lang="ts">
	import { Button } from '$lib/components/ui/button/index.js';
	import Brush from '$lib/components/site/Brush.svelte';
	import Eyebrow from '$lib/components/site/Eyebrow.svelte';
	import { githubUrl } from '$lib/data/site';

	const code = 'font-mono text-[0.85em] bg-popover px-1.5 py-0.5 shadow-[inset_0_0_0_1px_var(--border)]';
	const link = 'text-primary underline underline-offset-4 hover:text-bright transition-colors';
</script>

<svelte:head>
	<title>Contributing — gscode</title>
	<meta name="description" content="How to contribute to the GSCode functions API" />
</svelte:head>

<article class="flex w-full flex-col items-center px-6 py-16">
	<div class="w-full max-w-[72ch] space-y-14 text-foreground">
		<!-- Hero -->
		<header class="space-y-4">
			<Eyebrow>Contributing · functions API</Eyebrow>
			<h1 class="text-3xl font-semibold tracking-[-.03em]">Contributing to the functions API</h1>
			<p class="font-sans text-[16.5px] leading-[1.66] font-light text-muted-foreground">
				GSCode's IntelliSense is powered by a community-led database of GSC and CSC function
				documentation. Every function description, parameter type, and usage example you see in
				the editor comes from this API. Your contributions directly improve the experience for
				every GSCode user.
			</p>
		</header>

		<!-- Getting Started -->
		<section class="space-y-4">
			<h2 class="text-xl font-semibold tracking-[-.02em]">Getting started</h2>
			<p class="font-sans text-[16.5px] leading-[1.66] font-light text-muted-foreground">
				All contributions are made through the web-based
				<a href="/editor" class={link}>API Editor</a>. To begin:
			</p>

			<div class="grid gap-3">
				<Brush cut={12} surface="card" tab="step 01" bodyClass="px-5 pt-7 pb-4">
					<p class="font-sans text-[16.5px] leading-[1.66] font-light">
						Open the <a href="/editor" class={link}>Editor</a> page.
					</p>
				</Brush>
				<Brush cut={12} surface="card" tab="step 02" bodyClass="px-5 pt-7 pb-4">
					<p class="font-sans text-[16.5px] leading-[1.66] font-light">
						Click <strong class="font-medium text-foreground">Load</strong> in the sidebar.
					</p>
				</Brush>
				<Brush cut={12} surface="card" tab="step 03" bodyClass="px-5 pt-7 pb-4">
					<p class="font-sans text-[16.5px] leading-[1.66] font-light">
						Under <strong class="font-medium text-foreground">Latest</strong>, select the library
						you want to work on &mdash; either <strong class="font-medium text-foreground">GSC</strong>
						or <strong class="font-medium text-foreground">CSC</strong>.
					</p>
				</Brush>
			</div>

			<p class="font-sans text-[16.5px] leading-[1.66] font-light text-muted-foreground">
				The sidebar will populate with every function in the library. You can search and filter
				functions using the search bar (<kbd class={code}>Ctrl+K</kbd>).
			</p>
		</section>

		<!-- Editing a Function -->
		<section class="space-y-4">
			<h2 class="text-xl font-semibold tracking-[-.02em]">Editing a function</h2>
			<p class="font-sans text-[16.5px] leading-[1.66] font-light text-muted-foreground">
				Click any function in the sidebar to open it in the editor. From here you can modify:
			</p>
			<ul
				class="marker:text-steel list-disc space-y-2 pl-6 font-sans text-[16.5px] leading-[1.66] font-light text-muted-foreground"
			>
				<li>
					<strong class="font-medium text-foreground">Name</strong> &mdash; the function name
					(should be PascalCase).
				</li>
				<li>
					<strong class="font-medium text-foreground">Description</strong> &mdash; a clear
					explanation of what the function does.
				</li>
				<li>
					<strong class="font-medium text-foreground">Overloads</strong> &mdash; functions can have
					multiple overloads, each with their own parameters and return type. You can add, remove,
					or duplicate overloads.
				</li>
				<li>
					<strong class="font-medium text-foreground">Parameters</strong> &mdash; for each overload,
					set the name, type, description, and whether the parameter is mandatory, optional, or
					variadic.
				</li>
				<li>
					<strong class="font-medium text-foreground">Return type</strong> &mdash; set the return
					type or mark the function as void.
				</li>
				<li>
					<strong class="font-medium text-foreground">Called on</strong> &mdash; if the function is
					called on an entity (e.g. <code class={code}>player</code>), specify the entity type.
				</li>
				<li>
					<strong class="font-medium text-foreground">Example</strong> &mdash; a code snippet
					showing how the function is used in practice.
				</li>
				<li>
					<strong class="font-medium text-foreground">Remarks</strong> &mdash; additional notes about
					behaviour, edge cases, or caveats.
				</li>
			</ul>
			<p class="font-sans text-[16.5px] leading-[1.66] font-light text-muted-foreground">
				You can also create entirely new functions using the
				<strong class="font-medium text-foreground">+</strong> button in the sidebar.
			</p>
		</section>

		<!-- Conventions -->
		<section class="space-y-4">
			<h2 class="text-xl font-semibold tracking-[-.02em]">Conventions</h2>
			<p class="font-sans text-[16.5px] leading-[1.66] font-light text-muted-foreground">
				To keep the API tidy and consistent, please follow these conventions. Contributions that
				don't follow them will be delayed or refused, so please get it right the first time.
			</p>

			<div class="space-y-3">
				<Brush cut={12} surface="card" tab="names" bodyClass="space-y-3 px-5 pt-7 pb-5">
					<p class="font-sans text-[16.5px] leading-[1.66] font-light">
						<strong class="font-medium text-foreground">Names</strong> &mdash; function names must
						be PascalCase. Capitalise at word boundaries. For initials, prefer lowercasing
						subsequent letters.
					</p>
					<div class="space-y-1.5 font-mono text-[13px]">
						<p>
							<span class="font-medium text-primary">Good:</span>
							<code class={code}>IPrintLnBold</code>, <code class={code}>SetLpf</code>
						</p>
						<p>
							<span class="font-medium text-destructive">Bad:</span>
							<code class={code}>iprintlnbold</code>, <code class={code}>IPrintLNBold</code>,
							<code class={code}>iPrintLnBold</code>, <code class={code}>SetLPF</code>
						</p>
					</div>
				</Brush>

				<Brush cut={12} surface="card" tab="descriptions" bodyClass="space-y-3 px-5 pt-7 pb-5">
					<p class="font-sans text-[16.5px] leading-[1.66] font-light">
						<strong class="font-medium text-foreground">Descriptions</strong> &mdash; function,
						called-on, parameter, return type, and remarks descriptions must be statement sentences
						in grammatically correct
						<strong class="font-medium text-foreground">American English</strong>.
					</p>
					<div class="space-y-1.5 font-mono text-[13px]">
						<p>
							<span class="font-medium text-primary">Good:</span>
							<code class={code}>"Initializes a struct at the given location."</code>
						</p>
						<p>
							<span class="font-medium text-destructive">Bad:</span>
							<code class={code}>"Initialises a struct at the given location."</code>,
							<code class={code}>"Is the weapon overheating?"</code>,
							<code class={code}>"returns true if weapon has ammo"</code>
						</p>
					</div>
				</Brush>

				<Brush cut={12} surface="card" tab="parameter names" bodyClass="space-y-3 px-5 pt-7 pb-5">
					<p class="font-sans text-[16.5px] leading-[1.66] font-light">
						<strong class="font-medium text-foreground">Parameter names</strong> &mdash; parameter,
						Called On, and return value names should be camelCase.
					</p>
					<div class="space-y-1.5 font-mono text-[13px]">
						<p>
							<span class="font-medium text-primary">Good:</span>
							<code class={code}>hasAmmo</code>
						</p>
						<p>
							<span class="font-medium text-destructive">Bad:</span>
							<code class={code}>has_ammo</code>, <code class={code}>hasammo</code>,
							<code class={code}>HasAmmo</code>
						</p>
					</div>
				</Brush>
			</div>

			<p class="font-sans text-[16.5px] leading-[1.66] font-light text-muted-foreground">
				The web editor will catch some mistakes for you, but please do not rely on it to catch
				everything.
			</p>
		</section>

		<!-- Verification & Validation -->
		<section class="space-y-4">
			<h2 class="text-xl font-semibold tracking-[-.02em]">Verification &amp; validation</h2>
			<p class="font-sans text-[16.5px] leading-[1.66] font-light text-muted-foreground">
				Functions in the API are either <strong class="font-medium text-foreground"
					>autogenerated</strong
				>
				(based on Treyarch's API or scraped game data, incomplete) or
				<strong class="font-medium text-foreground">verified</strong> (reviewed and confirmed by a
				contributor). These two flags are mutually exclusive.
			</p>
			<p class="font-sans text-[16.5px] leading-[1.66] font-light text-muted-foreground">
				Marking a function as <strong class="font-medium text-foreground">verified</strong> enforces
				stricter validation rules:
			</p>
			<ul
				class="marker:text-steel list-disc space-y-2 pl-6 font-sans text-[16.5px] leading-[1.66] font-light text-muted-foreground"
			>
				<li>The description must be a proper sentence (capitalised, ending with a period).</li>
				<li>Every parameter must have a type and description.</li>
				<li>A return type must be specified (or the function explicitly marked as void).</li>
				<li>Remarks, if present, must also be proper sentences.</li>
			</ul>
			<p class="font-sans text-[16.5px] leading-[1.66] font-light text-muted-foreground">
				The right sidebar of the editor shows validation errors in real-time. Resolve all errors
				before marking a function as verified.
			</p>
		</section>

		<!-- Submitting Changes -->
		<section class="space-y-4">
			<h2 class="text-xl font-semibold tracking-[-.02em]">Submitting your changes</h2>
			<p class="font-sans text-[16.5px] leading-[1.66] font-light text-muted-foreground">
				Once you've finished editing, export your work:
			</p>

			<div class="grid gap-3">
				<Brush cut={12} surface="card" tab="step 01" bodyClass="px-5 pt-7 pb-4">
					<p class="font-sans text-[16.5px] leading-[1.66] font-light">
						Click <strong class="font-medium text-foreground">Download</strong> in the sidebar to
						export the library as a JSON file.
					</p>
				</Brush>
				<Brush cut={12} surface="card" tab="step 02" bodyClass="px-5 pt-7 pb-4">
					<p class="font-sans text-[16.5px] leading-[1.66] font-light">
						Open a new issue on the
						<a href="{githubUrl}/issues/new" class={link} target="_blank" rel="noopener noreferrer">
							GSCode GitHub repository</a
						>.
					</p>
				</Brush>
				<Brush cut={12} surface="card" tab="step 03" bodyClass="px-5 pt-7 pb-4">
					<p class="font-sans text-[16.5px] leading-[1.66] font-light">
						Attach the downloaded JSON file to the issue and briefly describe what you changed.
					</p>
				</Brush>
			</div>

			<p class="font-sans text-[16.5px] leading-[1.66] font-light text-muted-foreground">
				A maintainer will review your changes and merge them into the next API revision.
			</p>
		</section>

		<!-- CTA -->
		<section class="flex flex-col items-center gap-4 pt-4">
			<Button href="/editor" size="lg">Open the editor</Button>
		</section>
	</div>
</article>
