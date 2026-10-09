<script lang="ts">
	/** Blackbird's Workshop preflight: three checks land, then Publish lights up. */
	import CheckIcon from '@lucide/svelte/icons/check';
	import { reducedMotion } from '$lib/actions/reveal';

	let { active = false, run = 0 }: { active?: boolean; run?: number } = $props();

	let passed = $state(0);
	let target = $state<'stable' | 'test'>('test');

	$effect(() => {
		if (!active) return;
		void run;
		if (reducedMotion()) {
			passed = 3;
			return;
		}
		passed = 0;
		const timers = [1, 2, 3].map((n) => setTimeout(() => (passed = n), 300 + n * 380));
		return () => timers.forEach(clearTimeout);
	});

	const checks = [
		['Title', 'Castle Redux'],
		['Images', 'preview + gallery'],
		['Build', 'zone\\zm_castle_redux.ff']
	];
	const segment = (on: boolean) =>
		`cursor-pointer rounded px-3 py-1 text-xs ${on ? 'bg-app-panel text-app-text' : 'text-app-dim hover:text-app-text'}`;
</script>

<div class="bg-app-panel border-app-edge font-app rounded-lg border p-4 text-[13px] font-normal">
	<div class="flex flex-wrap items-center justify-between gap-3">
		<span class="font-medium">Publish Castle Redux</span>
		<span class="bg-app-raise inline-flex rounded-md p-0.5" role="group" aria-label="Workshop version">
			<button type="button" class={segment(target === 'stable')} aria-pressed={target === 'stable'} onclick={() => (target = 'stable')}
				>Stable</button
			>
			<button type="button" class={segment(target === 'test')} aria-pressed={target === 'test'} onclick={() => (target = 'test')}
				>Test</button
			>
		</span>
	</div>
	<p class="text-app-dim mt-2 mb-3.5 text-xs">
		{target === 'test'
			? 'Workshop version: test. Your stable item stays as it is.'
			: 'Workshop version: stable. The one your players subscribe to.'}
	</p>
	<div class="flex flex-col gap-0.5">
		{#each checks as [label, value], i (label)}
			<div class="border-app-edge flex items-center gap-2.5 border-t py-[7px]">
				<span
					class="text-app-ok inline-flex size-[18px] items-center justify-center transition-opacity duration-200"
					style:opacity={passed > i ? 1 : 0.15}><CheckIcon class="size-[15px]" strokeWidth={2} /></span
				>{label}<span class="text-app-dim ml-auto">{value}</span>
			</div>
		{/each}
	</div>
	<div
		class="mt-3 flex h-[34px] items-center justify-center rounded-md font-medium transition-colors duration-200 {passed >= 3
			? 'bg-[var(--teal)] text-[var(--ink)]'
			: 'bg-app-raise text-app-dim'}"
	>
		Publish to {target === 'test' ? 'test item' : 'stable item'}
	</div>
</div>
