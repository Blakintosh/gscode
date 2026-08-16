<script lang="ts">
	import * as Breadcrumb from '$lib/components/ui/breadcrumb/index.js';
	import * as Code from '$lib/components/ui/code/index.js';
	import Flag from '@lucide/svelte/icons/flag';
	import Link from '@lucide/svelte/icons/link';
	import Check from '@lucide/svelte/icons/check';
	import TriangleAlert from '@lucide/svelte/icons/triangle-alert';
	import Button from '$components/ui/button/button.svelte';
	import { Badge } from '$lib/components/ui/badge/index.js';
	import CopyButton from '$components/ui/copy-button/copy-button.svelte';
	import Brush from '$lib/components/site/Brush.svelte';
	import GithubIcon from '$lib/components/site/GithubIcon.svelte';
	import FlagsAlert from '$components/app/pages/library/article/FlagsAlert.svelte';
	import ParameterEntry from '$components/app/pages/library/article/ParameterEntry.svelte';
	import { page } from '$app/state';
	import type { ScrFunction } from '$lib/models/library';
	import { siteUrl } from '$lib/data/site';
	import { overloadToSyntacticString, typeToString } from '$lib/util/scriptApi';

	let { name, description, example, remarks, overloads, flags }: ScrFunction = $derived(
		page.data.func as ScrFunction
	);

	const languageId = $derived((page.data.languageId as string) ?? 'gsc');
	const languageName = $derived(
		languageId === 'gsc' ? 'GSC' : languageId === 'csc' ? 'CSC' : 'Unknown'
	);

	const languageJsonFile = $derived(languageId === 'csc' ? 't7_api_csc.json' : 't7_api_gsc.json');

	/** Header chips: what the symbol is, at a glance. */
	const calledOnLabel = $derived.by(() => {
		const calledOn = overloads?.[0]?.calledOn;
		if (!calledOn) return '';
		return typeToString(calledOn.type) || (calledOn.name ?? '');
	});
	const returnLabel = $derived(
		overloads?.[0]?.returns?.void
			? 'void'
			: overloads?.[0]?.returns
				? typeToString(overloads[0].returns.type)
				: ''
	);
	const dangerFlag = $derived((flags ?? []).includes('unlisted'));

	/** Sora 600 sentence case — the only heading role a doc page gets. */
	const heading = 'text-foreground text-[17px] font-semibold tracking-[-.03em]';

	$effect(() => {
		document.title = `${name} - Script API Reference | GSCode`;
	});
</script>

<div class="mx-auto w-full max-w-5xl px-5 py-8 sm:px-8 lg:px-12 lg:py-12">
	<Breadcrumb.Root>
		<Breadcrumb.List class="font-mono text-[11px] tracking-[.14em] uppercase">
			<Breadcrumb.Item>
				<Breadcrumb.Link href="/library">Black Ops III</Breadcrumb.Link>
			</Breadcrumb.Item>
			<Breadcrumb.Separator />
			<Breadcrumb.Item>
				<Breadcrumb.Link href={`/library/${languageId}`}>{languageName}</Breadcrumb.Link>
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
				<Badge>{languageName}</Badge>
				{#if calledOnLabel}
					<Badge variant="secondary">Called on {calledOnLabel}</Badge>
				{/if}
				{#if returnLabel}
					<Badge variant="outline">Returns {returnLabel}</Badge>
				{/if}
				{#if dangerFlag}
					<Badge variant="destructive">Unlisted</Badge>
				{/if}
			</div>
		</div>

		<div class="flex shrink-0 items-center gap-1">
			<CopyButton
				variant="ghost"
				size="icon"
				aria-label="Copy a link to this function"
				title="Copy a link to this function"
				text={`${siteUrl}/library/${languageId}/${(name ?? '').toLowerCase()}`}
			>
				{#snippet child({ copied })}
					{#if copied}
						<Check class="text-primary size-4" />
					{:else}
						<Link class="size-4" />
					{/if}
				{/snippet}
			</CopyButton>
			<!-- Issue 36 is for GSC, issue 35 is for CSC -->
			<Button
				variant="ghost"
				size="icon"
				aria-label="Report an API issue"
				title="Report an API issue"
				href={languageName === 'GSC'
					? 'https://github.com/Blakintosh/gscode/issues/36'
					: 'https://github.com/Blakintosh/gscode/issues/35'}
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
				href={`https://github.com/Blakintosh/gscode/blob/main/site/src/lib/apiSource/${languageJsonFile}`}
				target="_blank"
				rel="noopener noreferrer"
			>
				<GithubIcon class="size-4" />
			</Button>
		</div>
	</header>

	<p class="text-muted-foreground mt-5 max-w-[72ch] text-[16.5px] leading-[1.65] font-light">
		{description}
	</p>

	<FlagsAlert {flags} class="mt-6 max-w-[72ch]" />

	<div class="mt-10 flex flex-col gap-10">
		{#each overloads as overload, index (index)}
			<section class="flex flex-col gap-6">
				<div class="flex flex-col gap-3">
					<h2 class={heading}>
						{overloads.length === 1 ? 'Signature' : `Signature (overload ${index + 1})`}
					</h2>
					<Brush
						surface="table"
						behind="background"
						cut={10}
						handles
						readout={languageName}
						class="max-w-[72ch]"
						bodyClass="overflow-x-auto px-4 py-3.5"
					>
						<code class="text-foreground block font-mono text-[13px] leading-[1.7] whitespace-pre-wrap [overflow-wrap:anywhere] lg:text-[14px]">
							{overloadToSyntacticString(name, overload)}
						</code>
					</Brush>
				</div>

				{#if overload.calledOn}
					<div class="flex flex-col gap-1">
						<h3 class={heading}>Called on</h3>
						<div class="max-w-[72ch]">
							<ParameterEntry {...overload.calledOn} />
						</div>
					</div>
				{/if}

				<div class="flex flex-col gap-1">
					<h3 class={heading}>Parameters</h3>
					{#if overload.parameters && overload.parameters.length}
						<div class="max-w-[72ch]">
							{#each overload.parameters as parameter, parameterIndex (parameterIndex)}
								<ParameterEntry {...parameter} />
							{/each}
						</div>
					{:else}
						<p class="text-dim mt-1 text-[14.5px] font-light">This function takes no parameters.</p>
					{/if}
				</div>

				<div class="flex flex-col gap-1">
					<h3 class={heading}>Returns</h3>
					{#if overload.returns}
						{#if !overload.returns.void}
							<div class="max-w-[72ch]">
								<ParameterEntry {...overload.returns} />
							</div>
						{:else}
							<p class="text-dim mt-1 text-[14.5px] font-light">
								This function does not return a value.
							</p>
						{/if}
					{:else}
						<p class="text-muted-foreground mt-1 flex items-center gap-2.5 text-[14.5px] font-light">
							<TriangleAlert class="text-dim size-4 shrink-0" />
							This function's return type is unknown.
						</p>
					{/if}
				</div>
			</section>
		{/each}

		{#if example}
			<section class="flex flex-col gap-3">
				<h2 class={heading}>Usage</h2>
				<div class="max-w-[72ch]">
					<Code.Root value="1" language={languageName}>
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

		{#if remarks && remarks.length}
			<section class="flex flex-col gap-3">
				<h2 class={heading}>Remarks</h2>
				<ul class="flex max-w-[72ch] flex-col gap-2.5">
					{#each remarks as remark, remarkIndex (remarkIndex)}
						<li class="text-muted-foreground flex gap-3 text-[15px] leading-[1.6] font-light">
							<i aria-hidden="true" class="bg-primary mt-[9px] block size-[6px] shrink-0"></i>
							<span>{remark}</span>
						</li>
					{/each}
				</ul>
			</section>
		{/if}
	</div>
</div>
