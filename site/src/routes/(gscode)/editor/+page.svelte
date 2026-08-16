<script lang="ts">
	import * as Breadcrumb from '$lib/components/ui/breadcrumb/index.js';
	import FileJson from '@lucide/svelte/icons/file-json';
	import Trash2 from '@lucide/svelte/icons/trash-2';
	import Plus from '@lucide/svelte/icons/plus';
	import Copy from '@lucide/svelte/icons/copy';
	import X from '@lucide/svelte/icons/x';
	import Button from '$components/ui/button/button.svelte';
	import Brush from '$lib/components/site/Brush.svelte';
	import ValidationStatus from '$components/app/pages/editor/article/ValidationStatus.svelte';
	import FlagEditor from '$components/app/pages/editor/article/FlagEditor.svelte';
	import EditName from '$components/app/pages/editor/article/EditName.svelte';
	import EditDescription from '$components/app/pages/editor/article/EditDescription.svelte';
	import EditExample from '$components/app/pages/editor/article/EditExample.svelte';
	import EditReturns from '$components/app/pages/editor/article/EditReturns.svelte';
	import EditCalledOn from '$components/app/pages/editor/article/EditCalledOn.svelte';
	import EditParameters from '$components/app/pages/editor/article/EditParameters.svelte';
	import EditRemarks from '$components/app/pages/editor/article/EditRemarks.svelte';
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

	/** Section heading — Sora 600, sentence case, sat on a hairline. */
	const sectionHeading =
		'border-border flex items-center justify-between border-b pb-2 text-base font-semibold tracking-[-.03em] lg:text-lg';
</script>

{#if !editor.hasLibrary}
	<!-- Empty state - no library loaded -->
	<div class="flex h-full flex-col items-center justify-center gap-7 px-8 text-center">
		<Brush surface="card" cut={12} handles bodyClass="p-5">
			<FileJson class="text-primary size-10" />
		</Brush>
		<div class="flex max-w-md flex-col items-center gap-3">
			<h1 class="text-xl font-semibold tracking-[-.03em]">No library loaded</h1>
			<p class="text-muted-foreground">
				Load a library JSON file to start editing function definitions. You can load from a file or
				pull from the latest API version.
			</p>
		</div>
		<p class="type-label text-dim">Use the sidebar to load a library</p>
	</div>
{:else if !functionEditor}
	<!-- Library loaded but no function selected -->
	<div class="flex h-full flex-col items-center justify-center gap-3 px-8 text-center">
		<h2 class="text-lg font-semibold tracking-[-.03em]">Select a function</h2>
		<p class="text-muted-foreground">Choose a function from the sidebar to start editing.</p>
	</div>
{:else}
	<!-- Function editor view -->
	<div
		class="flex w-full min-w-0 flex-col-reverse items-stretch gap-4 text-sm lg:h-full lg:min-h-0 lg:w-auto lg:flex-row lg:text-base"
	>
		<div class="grow overflow-y-auto px-6 lg:px-16">
			<Breadcrumb.Root>
				<Breadcrumb.List class="font-mono text-[11px] tracking-[.08em] uppercase">
					<Breadcrumb.Item>
						<Breadcrumb.Link class="hover:text-primary"
							>{editor.library?.gameId === 't7'
								? 'Black Ops III'
								: editor.library?.gameId}</Breadcrumb.Link
						>
					</Breadcrumb.Item>
					<Breadcrumb.Separator />
					<Breadcrumb.Item>
						<Breadcrumb.Link class="hover:text-primary">{languageName}</Breadcrumb.Link>
					</Breadcrumb.Item>
					<Breadcrumb.Separator />
					<Breadcrumb.Item>
						<Breadcrumb.Page>{name}</Breadcrumb.Page>
					</Breadcrumb.Item>
				</Breadcrumb.List>
			</Breadcrumb.Root>

			<div class="py-5">
				<div class="mb-2">
					<EditName {functionEditor} />
				</div>

				<EditDescription {functionEditor} />

				<div class="3xl:grid-cols-5 3xl:gap-8 grid min-h-0 grid-cols-1 gap-14 py-8">
					<div class="3xl:col-span-3 flex min-h-0 flex-col gap-6">
						<div class={sectionHeading}>
							<h2>
								{#if overloads.length === 1}
									Overload
								{:else}
									Overloads ({overloads.length})
								{/if}
							</h2>
							<Button variant="secondary" size="xs" onclick={() => functionEditor?.addOverload()}>
								<Plus />
								Add overload
							</Button>
						</div>

						{#each overloads as overload, index}
							<Brush
								surface="card"
								cut={12}
								tab={overloads.length === 1 ? 'Spec' : `Spec ${index + 1}`}
								bodyClass="flex flex-col gap-6 px-6 pt-9 pb-6"
							>
								<div class="flex items-start justify-between gap-3">
									<code
										class="bg-recess inset-edge chamfer chamfer-sm grow px-4 py-3 font-mono text-xs leading-relaxed break-all lg:text-sm"
									>
										{overloadToSyntacticString(name, overload)}
									</code>
									<div class="flex shrink-0 items-center gap-1">
										<Button
											variant="ghost"
											size="icon-sm"
											title="Duplicate overload"
											onclick={() => functionEditor?.duplicateOverload(index)}
										>
											<Copy />
											<span class="sr-only">Duplicate overload</span>
										</Button>
										{#if overloads.length > 1}
											<Button
												variant="ghost"
												size="icon-sm"
												class="hover:text-destructive"
												title="Remove overload"
												onclick={() => functionEditor?.removeOverload(index)}
											>
												<X />
												<span class="sr-only">Remove overload</span>
											</Button>
										{/if}
									</div>
								</div>

								<div class="flex flex-col gap-3">
									<h3 class="type-label text-dim">Called on entity</h3>
									<EditCalledOn {functionEditor} overloadIndex={index} />
								</div>

								<div class="flex flex-col gap-3">
									<h3 class="type-label text-dim">Parameters</h3>
									<EditParameters {functionEditor} overloadIndex={index} />
								</div>

								<div class="flex flex-col gap-3">
									<h3 class="type-label text-dim">Returns</h3>
									<EditReturns {functionEditor} overloadIndex={index} />
								</div>
							</Brush>
						{/each}
					</div>

					<div class="3xl:col-span-2 flex flex-col gap-4">
						<h2 class={sectionHeading}>Usage</h2>
						<EditExample {functionEditor} />

						<h2 class="{sectionHeading} mt-4">Remarks</h2>
						<EditRemarks {functionEditor} />
					</div>
				</div>
			</div>
		</div>

		<div class="border-border bg-card flex shrink-0 flex-col gap-6 px-5 py-6 lg:w-80 lg:border-l">
			<ValidationStatus {functionEditor} />
			<Separator />
			<FlagEditor {functionEditor} />
			<Separator />
			<div class="flex flex-col gap-2.5">
				<h3 class="type-label text-dim">Actions</h3>
				<Button
					variant="destructive"
					size="sm"
					class="w-full"
					onclick={() => {
						if (
							name &&
							confirm(
								`Are you sure you want to delete "${name}"? This can be undone before saving.`
							)
						) {
							editor.deleteFunction(name);
						}
					}}
				>
					<Trash2 />
					Delete function
				</Button>
			</div>
		</div>
	</div>
{/if}
