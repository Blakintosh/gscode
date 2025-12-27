<script lang="ts">
	import * as Breadcrumb from '$lib/components/ui/breadcrumb/index.js';
	// @ts-ignore
	import Flag from 'lucide-svelte/icons/flag';
	// @ts-ignore
	import Link from 'lucide-svelte/icons/link';
	// @ts-ignore
	import Check from 'lucide-svelte/icons/check';
	import Button from '$components/ui/button/button.svelte';
	import CopyButton from '$components/ui/copy-button/copy-button.svelte';
	import ValidationAlert from '$components/app/pages/editor/article/ValidationAlert.svelte';
	import EditName from '$components/app/pages/editor/article/EditName.svelte';
	import EditDescription from '$components/app/pages/editor/article/EditDescription.svelte';
	import EditExample from '$components/app/pages/editor/article/EditExample.svelte';
	import EditReturns from '$components/app/pages/editor/article/EditReturns.svelte';
	import EditCalledOn from '$components/app/pages/editor/article/EditCalledOn.svelte';
	import EditParameters from '$components/app/pages/editor/article/EditParameters.svelte';
	import { Separator } from '$lib/components/ui/separator/index.js';
	import { page } from '$app/stores';
	import type { ScrFunction } from '$lib/models/library';
	import type { FunctionEditor } from '$lib/api-editor/function-editor.svelte';
	import { onMount } from 'svelte';
	import { overloadToSyntacticString } from '$lib/util/scriptApi';
	import { getEditorContext } from '$lib/api-editor/editor.svelte';

	const editor = getEditorContext();
	let functionEditor: FunctionEditor = $derived(
		editor.getFunction($page.params.func) ?? [...editor.functions.values()][0]
	);

	let { name, remarks, overloads }: ScrFunction = $derived(functionEditor.function);
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
			document.title = `${name} - API Editor | GSCode`;
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
			<div class="mb-1">
				<EditName {functionEditor} />
			</div>

			<EditDescription {functionEditor} />

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

						<div class="flex flex-col gap-4">
							<h3 class="font-medium text-base lg:text-lg border-b py-2">Called on Entity</h3>
							<EditCalledOn {functionEditor} overloadIndex={index} />
						</div>

						<div class="flex flex-col gap-4">
							<h3 class="font-medium text-base lg:text-lg border-b py-2">Parameters</h3>
							<EditParameters {functionEditor} overloadIndex={index} />
						</div>

						<div class="flex flex-col gap-4">
							<h3 class="font-medium text-base lg:text-lg border-b py-2">Returns</h3>
							<EditReturns {functionEditor} overloadIndex={index} />
						</div>
					{/each}
				</div>

				<div class="flex flex-col gap-4 3xl:col-span-2">
					<h2 class="font-medium text-lg lg:text-xl border-b py-2">Usage</h2>
					<EditExample {functionEditor} />

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
			</div>
		</div>
	</div>

	<div class="flex flex-col shrink-0 px-4 border-l lg:w-80 gap-6">
		<div class="flex flex-col gap-4">
			<ValidationAlert {functionEditor} />

			<Separator />

			<div class="font-medium text-sm hidden lg:block">Actions</div>
			<div class="flex flex-row lg:flex-col gap-2">
				<CopyButton
					variant="secondary"
					size={'sm'}
					class="w-full gap-4"
					text={`https://gscode.net/editor/${languageName.toLowerCase()}/${name.toLowerCase()}`}
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
				<!-- Issue 36 is for GSC, issue 35 is for CSC -->
				<Button
					variant="secondary"
					size={'sm'}
					class="w-full gap-4"
					href={languageName === 'GSC'
						? 'https://github.com/Blakintosh/gscode/issues/36'
						: 'https://github.com/Blakintosh/gscode/issues/35'}
					target="_blank"
					rel="noopener noreferrer"
				>
					<Flag class="w-4 h-4" />
					<span class="hidden lg:block">Report an API issue</span>
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
	</div>
</div>
