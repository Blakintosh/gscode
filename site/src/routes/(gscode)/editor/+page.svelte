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
	// @ts-ignore
	import Trash2 from 'lucide-svelte/icons/trash-2';
	// @ts-ignore
	import Plus from 'lucide-svelte/icons/plus';
	// @ts-ignore
	import Copy from 'lucide-svelte/icons/copy';
	// @ts-ignore
	import X from 'lucide-svelte/icons/x';
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
				pull from the latest API version.
			</p>
		</div>
		<div class="flex flex-col gap-2 text-sm text-muted-foreground">
			<p>Use the sidebar to load a library.</p>
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
						<div class="flex items-center justify-between">
							<h2 class="font-medium text-lg lg:text-xl">
								{#if overloads.length === 1}
									Overload
								{:else}
									Overloads ({overloads.length})
								{/if}
							</h2>
							<Button
								variant="outline"
								size="sm"
								onclick={() => functionEditor?.addOverload()}
							>
								<Plus class="h-4 w-4 mr-2" />
								Add Overload
							</Button>
						</div>

						{#each overloads as overload, index}
							<div class="flex flex-col gap-4">
								<div class="flex items-center justify-between border-b py-2">
									<h2 class="font-medium text-lg lg:text-xl">
										{#if overloads.length === 1}
											Specification
										{:else}
											Specification (Overload {index + 1})
										{/if}
									</h2>
									<div class="flex items-center gap-1">
										<Button
											variant="ghost"
											size="sm"
											class="h-7 w-7 p-0"
											title="Duplicate overload"
											onclick={() => functionEditor?.duplicateOverload(index)}
										>
											<Copy class="h-4 w-4" />
										</Button>
										{#if overloads.length > 1}
											<Button
												variant="ghost"
												size="sm"
												class="h-7 w-7 p-0 text-destructive hover:text-destructive"
												title="Remove overload"
												onclick={() => functionEditor?.removeOverload(index)}
											>
												<X class="h-4 w-4" />
											</Button>
										{/if}
									</div>
								</div>
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

							{#if index < overloads.length - 1}
								<Separator class="my-4" />
							{/if}
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
			<Separator />
			<div class="flex flex-col gap-2">
				<h3 class="font-medium text-sm">Actions</h3>
				<Button
					variant="destructive"
					size="sm"
					class="w-full"
					onclick={() => {
						if (name && confirm(`Are you sure you want to delete "${name}"? This can be undone before saving.`)) {
							editor.deleteFunction(name);
						}
					}}
				>
					<Trash2 class="w-4 h-4 mr-2" />
					Delete Function
				</Button>
			</div>
		</div>
	</div>
{/if}
