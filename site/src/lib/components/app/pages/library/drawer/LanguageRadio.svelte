<script lang="ts">
	import { page } from '$app/state';
	import { languagesFor, type GameEntry } from '$lib/data/games';
	import { cn } from '$lib/utils.js';

	type Props = {
		onLanguageChange: (value: string | undefined) => void;
	};

	let { onLanguageChange }: Props = $props();

	// Only three of the five games ship client scripts, so CSC is not always a real choice. Offering
	// it where it does not exist would navigate to a 404.
	const languages = $derived.by(() => {
		const game = page.data.game as GameEntry | undefined;
		return game ? languagesFor(game) : (['gsc'] as const);
	});

	const current = $derived(page.data.languageId as string | undefined);
</script>

<!-- Segmented control: a recess split evenly, mono uppercase, active is the teal chip. -->
<div
	class="bg-recess inset-edge chamfer chamfer-2xs grid gap-px p-px"
	style={`grid-template-columns: repeat(${languages.length}, minmax(0, 1fr))`}
	role="radiogroup"
>
	{#each languages as language (language)}
		<button
			type="button"
			role="radio"
			aria-checked={current === language}
			class={cn(
				'type-label chamfer chamfer-2xs cursor-pointer py-2.5 transition-colors outline-none',
				current === language
					? 'bg-primary text-primary-foreground'
					: 'text-muted-foreground hover:bg-[var(--wash-hover)] hover:text-foreground focus-visible:text-primary'
			)}
			onclick={() => onLanguageChange(language)}
		>
			{language.toUpperCase()}
		</button>
	{/each}
</div>
