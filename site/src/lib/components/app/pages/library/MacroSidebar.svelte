<script lang="ts">
	import { Input } from '$lib/components/ui/input/index.js';

	import Search from '@lucide/svelte/icons/search';

	import LanguageRadio from './drawer/LanguageRadio.svelte';
	import GamePicker from './drawer/GamePicker.svelte';
	import FileCombobox from './drawer/FileCombobox.svelte';
	import type { GshLibrary, GshMacro, GshMacroKind } from '$lib/models/macros';
	import { page } from '$app/state';
	import { games, languagesFor, type GameEntry } from '$lib/data/games';
	import { goto } from '$app/navigation';
	import { cn } from '$lib/utils.js';

	type Props = {
		/** Called after a row is picked — lets the mobile sheet close itself. */
		onNavigate?: () => void;
	};

	let { onNavigate }: Props = $props();

	const truncateString = (string = '', maxLength = 20) =>
		string.length > maxLength ? `${string.substring(0, maxLength)}…` : string;

	const game = $derived(page.data.game as GameEntry);
	const library = $derived(page.data.library as GshLibrary);
	const files = $derived(page.data.files as string[]);

	async function onLanguageChange(value: string | undefined) {
		if (!value || value === 'gsh') {
			return;
		}

		await goto(`/library/${game.slug}/${value}`);
		onNavigate?.();
	}

	async function onGameChange(slug: string | undefined) {
		if (!slug || slug === game.slug) {
			return;
		}

		const next = games.find((entry) => entry.slug === slug);
		if (!next) {
			return;
		}

		// Only Black Ops III documents macros, so any other game lands on its function library —
		// the same over-specified-URL fallback the language routes use.
		const surface = next.hasMacros ? 'gsh' : languagesFor(next)[0];
		await goto(`/library/${slug}/${surface}`);
		onNavigate?.();
	}

	let searchTerm = $state('');
	let fileFilter = $state<string[]>([]);

	const filtered = $derived.by(() => {
		const w = searchTerm.replace(/[.+^${}()|[\]\\]/g, '\\$&'); // regexp escape
		const re = new RegExp(`^${w.replace(/\*/g, '.*').replace(/\?/g, '.')}$`, 'i');
		const term = searchTerm.toLowerCase();
		const wanted = new Set(fileFilter);

		return library.macros.filter((macro) => {
			if (
				wanted.size > 0 &&
				!macro.definitions.some((definition) => wanted.has(definition.path))
			) {
				return false;
			}
			return macro.name.toLowerCase().includes(term) || re.test(macro.name);
		});
	});

	// The reference's two sections — argument-taking macros and bare words — with the compiler's
	// own names ahead of both. A section that filters empty disappears rather than sitting as a
	// heading over nothing; built-ins live in no header, so any file filter hides them.
	const sections = $derived(
		(
			[
				['builtin', 'Built-in'],
				['function', 'Function macros'],
				['constant', 'Constants']
			] as [GshMacroKind, string][]
		)
			.map(([kind, label]) => ({
				kind,
				label,
				entries: filtered.filter((macro) => macro.kind === kind)
			}))
			.filter((section) => section.entries.length > 0)
	);

	/** The article shows the first macro when the URL has no `macro` segment. */
	const activeMacro = $derived(
		((page.data.macro as GshMacro | undefined)?.name ?? page.params.macro ?? '').toLowerCase()
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
			<p class="type-label text-dim">Game</p>
			<GamePicker {onGameChange} />
		</div>

		<div class="flex flex-col gap-2">
			<p class="type-label text-dim">Language</p>
			<LanguageRadio {onLanguageChange} />
		</div>

		<div class="relative w-full">
			<Input
				type="search"
				placeholder="Search macros"
				class="h-10 pr-14 pl-10 text-sm"
				bind:value={searchTerm}
				bind:ref={inputElement}
			/>
			<Search
				class="text-dim pointer-events-none absolute top-1/2 left-3.5 size-4 -translate-y-1/2"
			/>
			<span
				class="text-dim pointer-events-none absolute top-1/2 right-3 -translate-y-1/2 font-mono text-2xs tracking-widest"
			>
				CTRL K
			</span>
		</div>

		<div class="flex flex-col gap-2">
			<p class="type-label text-dim">Header files</p>
			<FileCombobox {files} value={fileFilter} onValueChange={(value) => (fileFilter = value)} />
		</div>
	</div>

	<div class="flex shrink-0 items-baseline justify-between px-4 pt-4 pb-1">
		<p class="type-label text-dim">Macros</p>
		<span class="type-data text-dim text-2xs">{filtered.length}</span>
	</div>
	<nav class="min-h-0 grow overflow-y-auto pb-4" aria-label="Preprocessor macros">
		{#each sections as section (section.kind)}
			<p class="type-label text-dim px-4 pt-3 pb-1">{section.label}</p>
			{#each section.entries as macro (macro.name)}
				{@const slug = macro.name.toLowerCase()}
				<a
					href={`/library/${game.slug}/gsh/${slug}`}
					aria-current={slug === activeMacro ? 'page' : undefined}
					onclick={() => onNavigate?.()}
					class={cn(
						'nav-item block truncate px-4 py-1.5 font-mono text-sm outline-none',
						slug === activeMacro ? 'nav-item-active' : 'hover:nav-item-hover',
						'focus-visible:nav-item-hover'
					)}
				>
					{truncateString(macro.name, 28)}
				</a>
			{/each}
		{:else}
			<p class="text-dim px-4 py-6 text-sm font-light">No macros match that search.</p>
		{/each}
	</nav>

	<div
		class="border-border text-dim flex shrink-0 items-center justify-between gap-2 border-t px-4 py-3.5 font-mono text-2xs tracking-widest uppercase"
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
