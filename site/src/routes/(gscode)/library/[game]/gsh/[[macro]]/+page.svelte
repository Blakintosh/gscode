<script lang="ts">
	import * as Breadcrumb from '$lib/components/ui/breadcrumb/index.js';
	import * as Code from '$lib/components/ui/code/index.js';
	import Cpu from '@lucide/svelte/icons/cpu';
	import Flag from '@lucide/svelte/icons/flag';
	import Link from '@lucide/svelte/icons/link';
	import Check from '@lucide/svelte/icons/check';
	import TriangleAlert from '@lucide/svelte/icons/triangle-alert';
	import Button from '$components/ui/button/button.svelte';
	import { Badge } from '$lib/components/ui/badge/index.js';
	import CopyButton from '$components/ui/copy-button/copy-button.svelte';
	import Brush from '$lib/components/site/Brush.svelte';
	import GithubIcon from '$lib/components/site/GithubIcon.svelte';
	import FlagAlert from '$components/app/pages/library/article/FlagAlert.svelte';
	import ParameterEntry from '$components/app/pages/library/article/ParameterEntry.svelte';
	import { page } from '$app/state';
	import type { GshMacro, GshMacroDefinition } from '$lib/models/macros';
	import type { GameEntry } from '$lib/data/games';
	import { siteUrl } from '$lib/data/site';

	let { name, kind, description, definitions, example, remarks, confidence }: GshMacro = $derived(
		page.data.macro as GshMacro
	);

	const game = $derived(page.data.game as GameEntry);
	const libraryPath = $derived(`/library/${game.slug}/gsh`);

	const kindLabel = $derived(
		kind === 'function' ? 'Function macro' : kind === 'builtin' ? 'Built-in' : 'Constant'
	);

	/** The directive as the header spells it, reconstructed for display. */
	function directiveFor(definition: GshMacroDefinition): string {
		const parameters = definition.parameters?.length
			? `(${definition.parameters.map((parameter) => parameter.name).join(', ')})`
			: '';
		const body = definition.expansion ? ` ${definition.expansion}` : '';
		return `#define ${name}${parameters}${body}`;
	}

	/** Parameter lists agree across a macro's definitions; differences live in the remarks. */
	const parameters = $derived(definitions[0]?.parameters ?? null);

	/** Sora 600 sentence case — the only heading role a doc page gets. */
	const heading = 'text-foreground text-lg font-semibold tracking-heading';

	$effect(() => {
		document.title = `${name} - ${game.shortName} Macro Reference | GSCode`;
	});
</script>

<div class="mx-auto w-full max-w-5xl px-5 py-8 sm:px-8 lg:px-12 lg:py-12">
	<Breadcrumb.Root>
		<Breadcrumb.List class="font-mono text-2xs tracking-label uppercase">
			<Breadcrumb.Item>
				<Breadcrumb.Link href={`/library/${game.slug}`}>{game.name}</Breadcrumb.Link>
			</Breadcrumb.Item>
			<Breadcrumb.Separator />
			<Breadcrumb.Item>
				<Breadcrumb.Link href={libraryPath}>GSH</Breadcrumb.Link>
			</Breadcrumb.Item>
			<Breadcrumb.Separator />
			<Breadcrumb.Item>
				<Breadcrumb.Page>{name}</Breadcrumb.Page>
			</Breadcrumb.Item>
		</Breadcrumb.List>
	</Breadcrumb.Root>

	<header class="mt-5 flex flex-wrap items-start justify-between gap-x-6 gap-y-4">
		<div class="min-w-0">
			<h1 class="text-foreground font-mono text-xl leading-tight break-words lg:text-2xl">
				{name}
			</h1>

			<div class="mt-3 flex flex-wrap items-center gap-1.5">
				<Badge>{game.shortName} GSH</Badge>
				<Badge variant="secondary">{kindLabel}</Badge>
				{#if definitions.length > 1}
					<Badge variant="outline">{definitions.length} definitions</Badge>
				{/if}
			</div>
		</div>

		<div class="flex shrink-0 items-center gap-1">
			<CopyButton
				variant="ghost"
				size="icon"
				aria-label="Copy a link to this macro"
				title="Copy a link to this macro"
				text={`${siteUrl}${libraryPath}/${(name ?? '').toLowerCase()}`}
			>
				{#snippet child({ copied })}
					{#if copied}
						<Check class="text-primary size-4" />
					{:else}
						<Link class="size-4" />
					{/if}
				{/snippet}
			</CopyButton>
			<Button
				variant="ghost"
				size="icon"
				aria-label="Report a macro documentation issue"
				title="Report a macro documentation issue"
				href="https://github.com/Blakintosh/gscode/issues"
				target="_blank"
				rel="noopener noreferrer"
			>
				<Flag class="size-4" />
			</Button>
			<Button
				variant="ghost"
				size="icon"
				aria-label="Edit this entry on GitHub"
				title="Edit this entry on GitHub"
				href="https://github.com/Blakintosh/gscode/blob/main/data/macros/t7_macros_gsh.json"
				target="_blank"
				rel="noopener noreferrer"
			>
				<GithubIcon class="size-4" />
			</Button>
		</div>
	</header>

	<p class="text-muted-foreground mt-5 max-w-[72ch] text-body font-light">
		{description}
	</p>

	{#if kind === 'builtin'}
		<div class="mt-6 max-w-[72ch]">
			<FlagAlert
				Icon={Cpu}
				title="Built into the compiler"
				description="No header defines this macro; the compiler substitutes it wherever the name is written."
			/>
		</div>
	{/if}

	{#if confidence === 'medium' || confidence === 'low'}
		<div class="mt-3 max-w-[72ch]">
			<FlagAlert
				Icon={TriangleAlert}
				title="Interpreted entry"
				description="This macro's purpose was inferred from its definition, naming and surrounding code rather than stated documentation, so details may be imprecise."
			/>
		</div>
	{/if}

	<div class="mt-10 flex flex-col gap-10">
		{#if definitions.length}
			<section class="flex flex-col gap-4">
				<h2 class={heading}>{definitions.length === 1 ? 'Definition' : 'Definitions'}</h2>
				{#each definitions as definition (`${definition.path}:${definition.line}`)}
					<div class="flex flex-col gap-2">
						<p class="text-dim font-mono text-2xs tracking-label">
							{definition.path}:{definition.line}
						</p>
						<Brush
							surface="table"
							behind="background"
							cut={10}
							handles
							readout="GSH"
							class="max-w-[72ch]"
							bodyClass="overflow-x-auto px-4 py-3.5"
						>
							<code
								class="text-foreground block font-mono text-sm leading-prose whitespace-pre-wrap [overflow-wrap:anywhere]"
							>
								{directiveFor(definition)}
							</code>
						</Brush>
					</div>
				{/each}
			</section>
		{/if}

		{#if kind === 'function' && parameters && parameters.length}
			<section class="flex flex-col gap-1">
				<h2 class={heading}>Parameters</h2>
				<div class="max-w-[72ch]">
					{#each parameters as parameter (parameter.name)}
						<ParameterEntry name={parameter.name} description={parameter.description} />
					{/each}
				</div>
			</section>
		{/if}

		{#if example}
			<section class="flex flex-col gap-3">
				<h2 class={heading}>Usage</h2>
				<div class="max-w-[72ch]">
					<Code.Root value="1" language="GSC">
						<Code.Tabs>
							<Code.Tab value="1">Example</Code.Tab>
						</Code.Tabs>
						<Code.Example value="1">
							<Code.Block code={example} />
						</Code.Example>
					</Code.Root>
				</div>
			</section>
		{/if}

		{#if remarks}
			<section class="flex flex-col gap-3">
				<h2 class={heading}>Remarks</h2>
				<p class="text-muted-foreground max-w-[72ch] text-base leading-relaxed font-light">
					{remarks}
				</p>
			</section>
		{/if}
	</div>
</div>
