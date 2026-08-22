<script lang="ts">
	import { page } from '$app/state';
	import * as Select from '$lib/components/ui/select/index.js';
	import { games, type GameEntry } from '$lib/data/games';

	type Props = {
		onGameChange: (value: string | undefined) => void;
	};

	let { onGameChange }: Props = $props();

	const current = $derived((page.data.game as GameEntry | undefined)?.slug);
	const currentLabel = $derived(
		games.find((game) => game.slug === current)?.shortName ?? 'Select a game'
	);
</script>

<Select.Root type="single" value={current} onValueChange={onGameChange}>
	<Select.Trigger class="w-full" aria-label="Game">
		<span class="type-label truncate">{currentLabel}</span>
	</Select.Trigger>
	<Select.Content>
		{#each games as game (game.slug)}
			<Select.Item value={game.slug} label={game.shortName}>
				<span class="flex w-full items-baseline justify-between gap-3">
					<span class="truncate">{game.name}</span>
					<span class="text-dim shrink-0 font-mono text-2xs">{game.year}</span>
				</span>
			</Select.Item>
		{/each}
	</Select.Content>
</Select.Root>
