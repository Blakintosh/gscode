<script lang="ts">
	/**
	 * The multi-game story: five dialects, one server. Steps through the games once when it
	 * lands (settling on Black Ops III), then the chips are live — pick a game and the
	 * settings line, the sample and the dialect facts follow.
	 */
	import * as Code from '$lib/components/ui/code';
	import { reducedMotion } from '$lib/actions/reveal';

	let { active = false }: { active?: boolean } = $props();

	type Game = {
		id: string;
		title: string;
		year: number;
		imports: string;
		functions: string;
		code: string;
	};

	const games: Game[] = [
		{
			id: 'cod4',
			title: 'Call of Duty 4',
			year: 2007,
			imports: '#include merges the file',
			functions: 'bare definitions',
			code: `#include maps\\_utility;

main()
{
 maps\\_load::main();
 level thread setup_hostages();
}`
		},
		{
			id: 'waw',
			title: 'World at War',
			year: 2008,
			imports: '#include merges the file',
			functions: 'bare definitions',
			code: `#include maps\\_utility;
#include maps\\_zombiemode_utility;

main()
{
 maps\\_zombiemode::main();
 level thread power_switch();
}`
		},
		{
			id: 'mw2',
			title: 'Modern Warfare 2',
			year: 2009,
			imports: '#include merges the file',
			functions: 'bare definitions',
			code: `#include common_scripts\\utility;

main()
{
 maps\\_load::main();
 level thread objective_track();
}`
		},
		{
			id: 'bo1',
			title: 'Black Ops',
			year: 2010,
			imports: '#include merges the file',
			functions: 'bare definitions',
			code: `#include maps\\_utility;
#include maps\\_zombiemode_utility;

main()
{
 maps\\_zombiemode::main();
 level thread pack_a_punch();
}`
		},
		{
			id: 'bo3',
			title: 'Black Ops III',
			year: 2015,
			imports: '#using names a namespace',
			functions: 'function keyword · :: calls',
			code: `#using scripts\\shared\\util_shared;
#namespace zm_mymap;

function main()
{
 util::wait_network_frame();
 level thread power_switch();
}`
		}
	];

	let index = $state(4);
	let touched = $state(false);
	const game = $derived(games[index]);

	$effect(() => {
		if (!active || touched) return;
		if (reducedMotion()) return;
		// One pass through the list, 900ms a step, resting on BO3.
		const timers = games.map((_, i) => setTimeout(() => (index = i), 200 + i * 900));
		return () => timers.forEach(clearTimeout);
	});
</script>

<div class="grid grid-cols-1 gap-5 lg:grid-cols-[minmax(0,1fr)_minmax(0,1.15fr)]">
	<div class="flex flex-col gap-4">
		<!-- The status-bar game switch, as a mono segmented list. -->
		<div role="tablist" aria-label="Game" class="bg-recess inset-edge chamfer flex flex-wrap [--cut:8px]">
			{#each games as g, i (g.id)}
				<button
					type="button"
					role="tab"
					aria-selected={i === index}
					onclick={() => {
						touched = true;
						index = i;
					}}
					class="chip-cut min-w-[64px] grow px-3 py-2 font-mono text-2xs tracking-label uppercase transition-colors {i === index
						? 'bg-primary text-primary-foreground'
						: 'text-muted-foreground hover:text-primary'}"
				>
					{g.id}
				</button>
			{/each}
		</div>

		<div class="bg-recess inset-edge px-4 py-3 font-mono text-sm">
			<span class="text-dim">"gscode.game"</span><span class="text-muted-foreground">:</span>
			<span class="text-primary">"{game.id}"</span>
		</div>

		<dl class="grid grid-cols-[auto_1fr] gap-x-5 gap-y-2 text-sm">
			<dt class="type-label text-dim self-center">Game</dt>
			<dd class="text-foreground">{game.title} <span class="text-dim font-mono text-2xs">{game.year}</span></dd>
			<dt class="type-label text-dim self-center">Imports</dt>
			<dd class="text-muted-foreground">{game.imports}</dd>
			<dt class="type-label text-dim self-center">Functions</dt>
			<dd class="text-muted-foreground">{game.functions}</dd>
			<dt class="type-label text-dim self-center">Data</dt>
			<dd class="text-muted-foreground">that game's builtins, object fields and Radiant keys</dd>
		</dl>
	</div>

	{#key game.id}
		<Code.Root value="1" language={game.id.toUpperCase()} class="my-0" behind="background">
			<Code.Tabs><Code.Tab value="1">{game.id === 'bo3' ? 'zm_mymap.gsc' : 'mymap.gsc'}</Code.Tab></Code.Tabs>
			<Code.Example value="1"><Code.Block code={game.code} /></Code.Example>
		</Code.Root>
	{/key}
</div>
