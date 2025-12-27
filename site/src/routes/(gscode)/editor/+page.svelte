<script lang="ts">
	import * as Breadcrumb from '$lib/components/ui/breadcrumb/index.js';
	// @ts-ignore
	import Flag from 'lucide-svelte/icons/flag';
	// @ts-ignore
	import Link from 'lucide-svelte/icons/link';
	// @ts-ignore
	import Check from 'lucide-svelte/icons/check';
	// @ts-ignore
	import FileJson from 'lucide-svelte/icons/file-json';
	import Button from '$components/ui/button/button.svelte';
	import CopyButton from '$components/ui/copy-button/copy-button.svelte';
	import ValidationStatus from '$components/app/pages/editor/article/ValidationStatus.svelte';
	import FlagEditor from '$components/app/pages/editor/article/FlagEditor.svelte';
	import EditName from '$components/app/pages/editor/article/EditName.svelte';
	import EditDescription from '$components/app/pages/editor/article/EditDescription.svelte';
	import EditExample from '$components/app/pages/editor/article/EditExample.svelte';
	import EditReturns from '$components/app/pages/editor/article/EditReturns.svelte';
	import EditCalledOn from '$components/app/pages/editor/article/EditCalledOn.svelte';
	import EditParameters from '$components/app/pages/editor/article/EditParameters.svelte';
	import { Separator } from '$lib/components/ui/separator/index.js';
	import type { ScrFunction } from '$lib/models/library';
	import type { FunctionEditor } from '$lib/api-editor/function-editor.svelte';
	import { onMount } from 'svelte';
	import { overloadToSyntacticString } from '$lib/util/scriptApi';
	import { getEditorContext } from '$lib/api-editor/editor.svelte';

	const editor = getEditorContext();

	let functionEditor: FunctionEditor | undefined = $derived(editor.getSelectedFunction());

	let fn: ScrFunction | undefined = $derived(functionEditor?.function);
	let name = $derived(fn?.name ?? '');
	let remarks = $derived(fn?.remarks);
	let overloads = $derived(fn?.overloads ?? []);

	let languageName = $derived.by(() => {
		switch (editor.library?.languageId) {
			case 'gsc':
				return 'GSC';
			case 'csc':
				return 'CSC';
			default:
				return 'Unknown';
		}
	});

	let languageJsonFile = $derived.by(() => {
		switch (editor.library?.languageId) {
			case 'gsc':
				return 't7_api_gsc.json';
			case 'csc':
				return 't7_api_csc.json';
			default:
				return null;
		}
	});

	onMount(() => {
		$effect(() => {
			if (name) {
				document.title = `${name} - API Editor | GSCode`;
			} else {
				document.title = 'API Editor | GSCode';
			}
		});
	});
</script>

{#if !editor.hasLibrary}
	<!-- Empty state - no library loaded -->
	<div class="flex flex-col items-center justify-center h-full gap-6 text-center px-8">
		<div class="flex flex-col items-center gap-4">
			<div class="p-4 rounded-full bg-muted">
				<FileJson class="w-12 h-12 text-muted-foreground" />
			</div>
			<h1 class="text-2xl font-semibold">No Library Loaded</h1>
			<p class="text-muted-foreground max-w-md">
				Load a library JSON file to start editing function definitions. You can load from a file or
				use one of the built-in API sources.
			</p>
		</div>
		<div class="flex flex-col gap-2 text-sm text-muted-foreground">
			<p>Use the sidebar to load a library:</p>
			<ul class="list-disc list-inside text-left">
				<li>Load from a JSON file</li>
				<li>Load latest GSC API</li>
				<li>Load latest CSC API</li>
			</ul>
		</div>
	</div>
{:else if !functionEditor}
	<!-- Library loaded but no function selected -->
	<div class="flex flex-col items-center justify-center h-full gap-4 text-center px-8">
		<h2 class="text-xl font-medium">Select a Function</h2>
		<p class="text-muted-foreground">
			Choose a function from the sidebar to start editing.
		</p>
	</div>
{:else}
	<!-- Function editor view -->
	<div
		class="flex flex-col-reverse lg:flex-row gap-4 items-stretch min-w-0 w-full lg:w-auto lg:h-full lg:min-h-0 text-sm lg:text-base"
	>
		<div class="grow px-6 lg:px-16 overflow-y-auto">
			<Breadcrumb.Root>
				<Breadcrumb.List class="text-xs lg:text-sm">
					<Breadcrumb.Item>
						<Breadcrumb.Link class="hover:text-foreground-muted"
							>{editor.library?.gameId === 't7' ? 'Black Ops III' : editor.library?.gameId}</Breadcrumb.Link
						>
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
			<ValidationStatus {functionEditor} />
			<Separator />
			<FlagEditor {functionEditor} />
		</div>
	</div>
{/if}
