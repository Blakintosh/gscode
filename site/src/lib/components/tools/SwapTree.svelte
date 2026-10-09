<script lang="ts">
	/**
	 * The swap: the BO3 folders as a file tree. One stock exe at a time is replaced by its
	 * gscode tool while the original drops into the hidden .gscode-backups folder above it.
	 */
	import FolderIcon from '@lucide/svelte/icons/folder';
	import { reducedMotion } from '$lib/actions/reveal';
	import { tools, type Tool } from '$lib/data/tools';

	let { active = false, run = 0 }: { active?: boolean; run?: number } = $props();

	let swapped = $state(0);
	$effect(() => {
		if (!active) return;
		void run;
		if (reducedMotion()) {
			swapped = 3;
			return;
		}
		swapped = 0;
		const timers = [1, 2, 3].map((n) => setTimeout(() => (swapped = n), 500 + (n - 1) * 900));
		return () => timers.forEach(clearTimeout);
	});

	interface Swap {
		file: string;
		stock: string;
		kept: string;
		tool: Tool;
		order: number;
		rule: string;
	}
	const bin: Swap[] = [
		{ file: 'asseteditor_modtools.exe', stock: 'APE · stock', kept: 'APE · kept', tool: tools.apex, order: 1, rule: 'border-eye' },
		{ file: 'modlauncher.exe', stock: 'Launcher · stock', kept: 'Launcher · kept', tool: tools.blackbird, order: 2, rule: 'border-burn' }
	];
	const sound: Swap[] = [
		{ file: 'snd_convert.exe', stock: 'stock', kept: 'kept', tool: tools.ultrasound, order: 3, rule: 'border-sonar' }
	];
</script>

{#snippet folder(name: string)}
	<div class="text-foreground flex items-center gap-2 py-1.5">
		<FolderIcon class="text-dim size-[15px]" />{name}
	</div>
{/snippet}

{#snippet group(swaps: Swap[])}
	<div class="border-border ml-[7px] border-l pl-3 sm:pl-6">
		<div class="text-dim flex items-center gap-2 py-1.5 pl-3">
			<FolderIcon class="size-[15px]" />.gscode-backups
			<span class="type-label ml-auto">hidden</span>
		</div>
		<div class="border-border ml-[19px] border-l border-dashed pl-3">
			{#each swaps as s (s.file)}
				{@const on = swapped >= s.order}
				<div
					class="text-muted-foreground flex flex-wrap items-center gap-x-2 gap-y-1 py-[5px] pl-3 transition-[opacity,translate] duration-[260ms,360ms] ease-out"
					style:opacity={on ? 1 : 0}
					style:translate={on ? '0 0' : '0 14px'}
				>
					<span class="min-w-0 [overflow-wrap:anywhere]">{s.file}</span>
					<span class="type-label text-dim ml-auto">{s.kept}</span>
				</div>
			{/each}
		</div>
		{#each swaps as s (s.file)}
			{@const on = swapped >= s.order}
			<div
				class="mt-1 flex flex-wrap items-center gap-x-2.5 gap-y-1 border-l-2 px-3 py-2 transition-[background-color,border-color] duration-300 {on
					? `${s.rule} bg-[var(--wash-active)]`
					: 'border-border'}"
			>
				<span class="text-foreground min-w-0 [overflow-wrap:anywhere]">{s.file}</span>
				<span class="ml-auto flex items-center gap-2">
					{#if on}
						<img src={s.tool.mascot} alt="" class="size-[22px]" />
						<span class="type-label {s.tool.ink}">{s.tool.name} {s.tool.version}</span>
					{:else}
						<span class="type-label text-dim">{s.stock}</span>
					{/if}
				</span>
			</div>
		{/each}
	</div>
{/snippet}

<div class="rimmed rimmed-recess chamfer">
	<div class="px-3 pt-5 pb-6 font-mono text-sm sm:px-6">
		<p class="type-label text-dim mb-3 flex justify-between gap-3">
			<span>Call of Duty Black Ops III</span>
			<span class="tabular-nums">{swapped} / 3 swapped</span>
		</p>
		{@render folder('<BO3>\\bin')}
		{@render group(bin)}
		<div class="mt-3">{@render folder('<BO3>\\sound')}</div>
		{@render group(sound)}
	</div>
</div>
