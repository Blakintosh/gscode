<script lang="ts">
	import * as Breadcrumb from '$lib/components/ui/breadcrumb/index.js';
	import * as Alert from '$lib/components/ui/alert/index.js';
	import * as Code from '$lib/components/ui/code/index.js';
	import * as Tabs from '$lib/components/ui/tabs/index.js';
	// @ts-ignore
	import Flag from 'lucide-svelte/icons/flag';
	// @ts-ignore
	import Link from 'lucide-svelte/icons/link';
	// @ts-ignore
	import TriangleAlert from 'lucide-svelte/icons/triangle-alert';
	// @ts-ignore
	import Check from 'lucide-svelte/icons/check';
	// @ts-ignore
	import Github from 'lucide-svelte/icons/github';
	import Button from '$components/ui/button/button.svelte';
	import CopyButton from '$components/ui/copy-button/copy-button.svelte';
	import FlagsAlert from '$components/app/pages/library/article/FlagsAlert.svelte';
	import { page } from '$app/stores';
	import type { ScrFunction } from '$lib/models/library';
	import ParameterEntry from '$components/app/pages/library/article/ParameterEntry.svelte';
	import { onMount } from 'svelte';
	import { overloadToSyntacticString } from '$lib/util/scriptApi';

	let { name, description, example, remarks, overloads, flags }: ScrFunction = $derived(
		$page.data.func as ScrFunction
	);
	let languageName = $derived.by(() => {
		switch ($page.data.languageId) {
			case 'gsc':
				return 'GSC';
			case 'csc':
				return 'CSC';
			default:
				return 'Unknown';
		}
	});

	let languageJsonFile = $derived.by(() => {
		switch ($page.data.languageId) {
			case 'gsc':
				return 't7_api_gsc.json';
			case 'csc':
				return 't7_api_csc.json';
		}
	});

	onMount(() => {
		$effect(() => {
			document.title = `${name} - Script API Reference | GSCode`;
		});
	});
</script>

<div
	class="flex flex-col-reverse lg:flex-row gap-4 items-stretch min-w-0 w-full lg:w-auto lg:h-full lg:min-h-0 text-sm lg:text-base"
>
	<div class="grow px-6 lg:px-16 overflow-y-auto">
		<Breadcrumb.Root>
			<Breadcrumb.List class="text-xs lg:text-sm">
				<Breadcrumb.Item>
					<Breadcrumb.Link class="hover:text-foreground-muted">Black Ops III</Breadcrumb.Link>
				</Breadcrumb.Item>
				<Breadcrumb.Separator />
				<Breadcrumb.Item>
					<Breadcrumb.Link class="hover:text-foreground-muted">{languageName}</Breadcrumb.Link>
				</Breadcrumb.Item>
				<Breadcrumb.Separator />
				<Breadcrumb.Item>
					<Breadcrumb.Page>{name}</Breadcrumb.Page>
				</Breadcrumb.Item>
			</Breadcrumb.List>
		</Breadcrumb.Root>

		<div class="py-4">
			<h1 class="scroll-m-20 text-xl font-bold tracking-tight lg:text-4xl mb-1">{name}</h1>

			<h2 class="text-base lg:text-xl text-muted-foreground">
				{description}
			</h2>

			<div class="grid grid-cols-1 3xl:grid-cols-5 3xl:gap-8 gap-16 py-8 min-h-0">
				<div class="3xl:col-span-3 flex flex-col gap-8 min-h-0">
					{#each overloads as overload, index}
						<div class="flex flex-col gap-4">
							<h2 class="font-medium text-lg lg:text-xl border-b py-2">
								{#if overloads.length === 1}
									Specification
								{:else}
									Specification (Overload {index + 1})
								{/if}
							</h2>
							<code class="font-mono bg-background border rounded-lg px-4 py-3 text-sm lg:text-lg">
								{overloadToSyntacticString(name, overload)}
							</code>
						</div>

						{#if overload.calledOn}
							<div class="flex flex-col gap-4">
								<h3 class="font-medium text-base lg:text-lg border-b py-2">Called on Entity</h3>
								<div class="divide-y">
									<ParameterEntry {...overload.calledOn} />
								</div>
							</div>
						{/if}

						<div class="flex flex-col gap-4">
							<h3 class="font-medium text-base lg:text-lg border-b py-2">Parameters</h3>
							{#if overload.parameters && overload.parameters.length}
								<div class="divide-y">
									{#each overload.parameters as parameter}
										<ParameterEntry {...parameter} />
									{/each}
								</div>
							{:else}
								<div class="text-sm">No parameters.</div>
							{/if}
						</div>

						<div class="flex flex-col gap-4">
							<h3 class="font-medium text-base lg:text-lg border-b py-2">Returns</h3>
							{#if overload.returns}
								{#if !overload.returns.void}
									<div class="divide-y">
										<ParameterEntry {...overload.returns} />
									</div>
								{:else}
									<div class="text-xs lg:text-sm">This function does not return a value.</div>
								{/if}
							{:else}
								<div class="text-sm flex gap-4 items-center">
									<TriangleAlert class="w-5 h-5 lg:w-6 lg:h-6" />
									<span class="italic text-xs lg:text-sm">
										This function's return type is unknown.
										<!-- Are you able to correct this? Help us <a href="#">fix this</a>. -->
									</span>
								</div>
							{/if}
						</div>
					{/each}
				</div>

				{#if example || remarks}
					<div class="flex flex-col gap-4 3xl:col-span-2">
						{#if example}
							<h2 class="font-medium text-lg lg:text-xl border-b py-2">Usage</h2>
							<Code.Root value={'1'}>
								<Code.Tabs>
									<Code.Tab value={'1'}>Example</Code.Tab>
								</Code.Tabs>
								<Code.Example value={'1'}>
									<Code.Block code={example} />
								</Code.Example>
							</Code.Root>
						{/if}

						{#if remarks}
							<h2 class="font-medium text-xl border-b py-2">Remarks</h2>
							<ul class="text-sm list-disc marker:text-muted-foreground pl-8">
								{#each remarks as remark}
									<li class="pl-4">
										{remark}
									</li>
								{/each}
							</ul>
						{/if}
					</div>
				{/if}
			</div>
		</div>
	</div>

	<div class="flex flex-col shrink-0 justify-between px-4 border-l lg:w-80">
		<div class="flex flex-col gap-4">
			<FlagsAlert {flags} />

			<div class="font-medium text-sm hidden lg:block">Actions</div>
			<div class="flex flex-row lg:flex-col gap-2">
				<CopyButton
					variant="secondary"
					size={'sm'}
					class="w-full gap-4"
					text={`https://gscode.net/library/${languageName.toLowerCase()}/${name.toLowerCase()}`}
				>
					{#snippet child({ copied })}
						{#if copied}
							<Check class="w-4 h-4" />
							<span class="hidden lg:block">Copied to clipboard</span>
						{:else}
							<Link class="w-4 h-4" />
							<span class="hidden lg:block">Share this function</span>
						{/if}
					{/snippet}
				</CopyButton>
				<Button
					variant="secondary"
					size={'sm'}
					class="w-full gap-4"
					href="https://github.com/Blakintosh/gscode/issues/new?assignees=&labels=bug&projects=&template=bug_report.md&title="
					target="_blank"
					rel="noopener noreferrer"
				>
					<Flag class="w-4 h-4" />
					<span class="hidden lg:block">Report an issue</span>
				</Button>
				<Button
					variant="secondary"
					size={'sm'}
					class="w-full gap-4"
					href={`https://github.com/Blakintosh/gscode/blob/main/site/src/lib/apiSource/${languageJsonFile}`}
					target="_blank"
					rel="noopener noreferrer"
				>
					<svg
						role="img"
						class="w-4 h-4"
						fill="currentColor"
						viewBox="0 0 24 24"
						xmlns="http://www.w3.org/2000/svg"
						><title>GitHub</title><path
							d="M12 .297c-6.63 0-12 5.373-12 12 0 5.303 3.438 9.8 8.205 11.385.6.113.82-.258.82-.577 0-.285-.01-1.04-.015-2.04-3.338.724-4.042-1.61-4.042-1.61C4.422 18.07 3.633 17.7 3.633 17.7c-1.087-.744.084-.729.084-.729 1.205.084 1.838 1.236 1.838 1.236 1.07 1.835 2.809 1.305 3.495.998.108-.776.417-1.305.76-1.605-2.665-.3-5.466-1.332-5.466-5.93 0-1.31.465-2.38 1.235-3.22-.135-.303-.54-1.523.105-3.176 0 0 1.005-.322 3.3 1.23.96-.267 1.98-.399 3-.405 1.02.006 2.04.138 3 .405 2.28-1.552 3.285-1.23 3.285-1.23.645 1.653.24 2.873.12 3.176.765.84 1.23 1.91 1.23 3.22 0 4.61-2.805 5.625-5.475 5.92.42.36.81 1.096.81 2.22 0 1.606-.015 2.896-.015 3.286 0 .315.21.69.825.57C20.565 22.092 24 17.592 24 12.297c0-6.627-5.373-12-12-12"
						/></svg
					>
					<span class="hidden lg:block">View on GitHub</span>
				</Button>
			</div>
		</div>
		<div class="flex flex-col gap-2">
			<!-- <div class="font-medium text-sm">
                See also
            </div>
            <div class="grid grid-flow-row auto-rows-max w-full">
                <Button variant="link" size={"sm"} class="justify-start font-normal text-muted-foreground">
                    IPrintLn
                </Button>
                <Button variant="link" size={"sm"} class="justify-start font-normal text-muted-foreground">
                    IPrintLn
                </Button>
                <Button variant="link" size={"sm"} class="justify-start font-normal text-muted-foreground">
                    IPrintLn
                </Button>
                <Button variant="link" size={"sm"} class="justify-start font-normal text-muted-foreground">
                    IPrintLn
                </Button>
            </div> -->
		</div>
	</div>
</div>
