<script lang="ts">
	import { page } from '$app/state';
	import { cn } from '$lib/utils.js';

	type Props = {
		onLanguageChange: (value: string | undefined) => void;
	};

	let { onLanguageChange }: Props = $props();

	const languages = ['gsc', 'csc'] as const;

	const current = $derived(page.data.languageId as string | undefined);
</script>

<!-- Segmented control: a recess split in two, mono uppercase, active is the teal chip. -->
<div class="bg-recess inset-edge chamfer chamfer-2xs grid grid-cols-2 gap-px p-px" role="radiogroup">
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
