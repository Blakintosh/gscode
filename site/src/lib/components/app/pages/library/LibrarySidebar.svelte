<script lang="ts">
	import { Input } from '$lib/components/ui/input/index.js';
	import { Skeleton } from '$lib/components/ui/skeleton/index.js';

	import Search from '@lucide/svelte/icons/search';

	import LanguageRadio from './drawer/LanguageRadio.svelte';
	import type { ScrFunction, ScrLibrary } from '$lib/models/library';
	import { page } from '$app/state';
	import { ApiLibrarian } from '$lib/app/library/api.svelte';
	import { goto } from '$app/navigation';
	import { cn } from '$lib/utils.js';

	type Props = {
		/** Called after a row is picked — lets the mobile sheet close itself. */
		onNavigate?: () => void;
	};

	let { onNavigate }: Props = $props();

	const skeletonRows = Array.from({ length: 12 }, (_, i) => i);

	const truncateString = (string = '', maxLength = 20) =>
		string.length > maxLength ? `${string.substring(0, maxLength)}…` : string;

	let librarian: ApiLibrarian = $state(page.data.librarian);

	let library: Promise<ScrLibrary> = $derived(librarian.library);

	async function onLanguageChange(value: string | undefined) {
		if (!value) {
			return;
		}

		librarian.languageId = value;
		await goto(`/library/${value}`);
		onNavigate?.();
	}

	let searchTerm = $state('');

	let filteredData = $derived.by(async () => {
		let w = searchTerm.replace(/[.+^${}()|[\]\\]/g, '\\$&'); // regexp escape
		const re = new RegExp(`^${w.replace(/\*/g, '.*').replace(/\?/g, '.')}$`, 'i');
		const resolvedLibrary = await library;

		return {
			entries: resolvedLibrary.api.filter((apiFunction: ScrFunction) => {
				return (
					apiFunction.name.toLowerCase().includes(searchTerm.toLowerCase()) ||
					re.test(apiFunction.name)
				);
			}),
			languageId: resolvedLibrary.languageId
		};
	});

	/** The article shows the first function when the URL has no `func` segment. */
	const activeFunction = $derived(
		((page.data.func as ScrFunction | undefined)?.name ?? page.params.func ?? '').toLowerCase()
	);

	let inputElement: HTMLInputElement | null = $state(null);
	function handleKeyDown(event: KeyboardEvent) {
		if (event.key === 'k' && (event.ctrlKey || event.metaKey) && inputElement) {
			event.preventDefault();
			inputElement.focus();
		}
	}
</script>

<div class="flex h-full min-h-0 flex-col">
	<div class="border-border flex shrink-0 flex-col gap-4 border-b px-4 py-5">
		<div class="flex flex-col gap-2">
			<p class="type-label text-dim tracking-[.2em]">Language</p>
			<LanguageRadio {onLanguageChange} />
		</div>

		<div class="relative w-full">
			<Search
				class="text-dim pointer-events-none absolute top-1/2 left-3.5 size-4 -translate-y-1/2"
			/>
			<Input
				type="search"
				placeholder="Search functions"
				class="h-10 pr-14 pl-10 text-[12.5px]"
				bind:value={searchTerm}
				bind:ref={inputElement}
			/>
			<span
				class="text-dim pointer-events-none absolute top-1/2 right-3 -translate-y-1/2 font-mono text-[10px] tracking-[.12em]"
			>
				CTRL K
			</span>
		</div>
	</div>

	{#await filteredData}
		<div class="flex min-h-0 grow flex-col gap-1 px-4 py-4">
			<Skeleton class="mb-2 h-[10px] w-24 [--cut:0px]" />
			{#each skeletonRows as i (i)}
				<Skeleton class="h-6 w-full [--cut:0px]" style={`opacity:${1 - i * 0.06}`} />
			{/each}
		</div>
	{:then data}
		<div class="flex shrink-0 items-baseline justify-between px-4 pt-4 pb-2">
			<p class="type-label text-dim tracking-[.2em]">Functions</p>
			<span class="type-data text-dim text-[10px]">{data.entries.length}</span>
		</div>
		<nav class="min-h-0 grow overflow-y-auto pb-4" aria-label="Script API functions">
			{#each data.entries as apiFunction (apiFunction.name)}
				{@const slug = apiFunction.name.toLowerCase()}
				<a
					href={`/library/${data.languageId}/${slug}`}
					aria-current={slug === activeFunction ? 'page' : undefined}
					onclick={() => onNavigate?.()}
					class={cn(
						'nav-item block truncate px-4 py-1.5 font-mono text-[12.5px] outline-none',
						slug === activeFunction ? 'nav-item-active' : 'hover:nav-item-hover',
						'focus-visible:nav-item-hover'
					)}
				>
					{truncateString(apiFunction.name, 28)}
				</a>
			{:else}
				<p class="text-dim px-4 py-6 text-[13.5px] font-light">No functions match that search.</p>
			{/each}
		</nav>
	{:catch}
		<div class="text-muted-foreground min-h-0 grow px-4 py-6 text-[13.5px] font-light">
			Something went wrong. Try reloading the page.
		</div>
	{/await}

	<div
		class="border-border text-dim flex shrink-0 items-center justify-between gap-2 border-t px-4 py-3.5 font-mono text-[10px] tracking-[.12em] uppercase"
	>
		<span>Part of GSCode</span>
		<a
			href="https://ko-fi.com/blakintosh"
			target="_blank"
			rel="noreferrer"
			class="hover:text-bright text-primary transition-colors"
		>
			Donate
		</a>
	</div>
</div>

<svelte:window onkeydown={handleKeyDown} />
