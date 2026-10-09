<script lang="ts">
	/**
	 * Apex's Ctrl+P: a filter query types itself and the list narrows to the weapons that
	 * match, the way the real palette filters as you type.
	 */
	import SearchIcon from '@lucide/svelte/icons/search';
	import { reducedMotion } from '$lib/actions/reveal';

	let { active = false, run = 0 }: { active?: boolean; run?: number } = $props();

	const query = 'type:weapon prop:damage>=100';
	const assets = [
		{ n: 'wpn_ar_havoc_zm_upgraded', t: 'weapon', dmg: 275 },
		{ n: 't7_weapon_ar_havoc_view', t: 'xmodel' },
		{ n: 'wpn_snp_locus_zm', t: 'weapon', dmg: 150 },
		{ n: 'mtl_marble_03', t: 'material' },
		{ n: 'wpn_ar_havoc_zm', t: 'weapon', dmg: 100 },
		{ n: 'wpn_ar_havoc_fire', t: 'sound' },
		{ n: 'wpn_smg_mp40_zm', t: 'weapon', dmg: 45 },
		{ n: 'fx_muzzle_flash_07', t: 'fx' },
		{ n: 'wpn_pistol_m1911_zm', t: 'weapon', dmg: 35 },
		{ n: 'mtl_dirt_06', t: 'material' },
		{ n: 'wpn_lmg_rpk_zm', t: 'weapon', dmg: 60 },
		{ n: 'zmb_castle_bell_toll', t: 'sound' },
		{ n: 'wpn_shotgun_pump_zm', t: 'weapon', dmg: 90 },
		{ n: 'i_castle_banner_c', t: 'image' },
		{ n: 't7_weapon_ar_havoc_world', t: 'xmodel' },
		{ n: 'wpn_ar_havoc_reload', t: 'sound' }
	];

	let typed = $state(0);
	const q = $derived(query.slice(0, typed));

	$effect(() => {
		if (!active) return;
		void run;
		if (reducedMotion()) {
			typed = query.length;
			return;
		}
		typed = 0;
		// Typing pauses at each space, the way a person finishes one filter before the next.
		let t = 350;
		const timers: ReturnType<typeof setTimeout>[] = [];
		for (let i = 1; i <= query.length; i++) {
			t += query[i - 1] === ' ' ? 520 : 58;
			timers.push(setTimeout(() => (typed = i), t));
		}
		return () => timers.forEach(clearTimeout);
	});

	const matches = $derived.by(() => {
		const type = q.match(/type:(\w*)/)?.[1];
		const minDamage = q.match(/prop:damage>=(\d+)/)?.[1];
		let rows = assets;
		if (type) rows = rows.filter((a) => a.t.startsWith(type));
		if (minDamage) rows = rows.filter((a) => (a.dmg ?? 0) >= +minDamage).sort((a, b) => b.dmg! - a.dmg!);
		return { rows, byDamage: !!minDamage };
	});
	const count = $derived(
		q.length === 0 ? '100k+ assets' : `${matches.rows.length} ${matches.rows.length === 1 ? 'match' : 'matches'}`
	);
</script>

<div
	class="bg-app-panel border-app-edge font-app flex h-full flex-col overflow-hidden rounded-lg border font-normal shadow-[0_18px_28px_rgba(0,0,0,.35)]"
>
	<div class="border-app-edge flex items-center gap-2.5 border-b px-3.5 py-3">
		<SearchIcon class="text-app-mute size-[15px]" />
		<span class="font-app-mono text-app-text min-w-0 flex-1 text-sm whitespace-pre-wrap [overflow-wrap:anywhere]"
			>{q}<span
				aria-hidden="true"
				class="caret bg-eye ml-px inline-block h-4 w-[1.5px] align-[-3px]"
			></span></span
		>
		<span class="text-app-dim text-xs whitespace-nowrap tabular-nums">{count}</span>
	</div>
	<div class="min-h-[296px] flex-1 p-1.5">
		{#each matches.rows.slice(0, 8) as a, i (a.n)}
			<div class="flex items-center gap-2.5 rounded-[5px] px-2.5 py-[7px] text-sm {i === 0 ? 'bg-app-raise' : ''}">
				<i class="marker {i === 0 ? 'bg-eye' : 'bg-app-edge'}"></i>
				<span class="text-app-text min-w-0 flex-1 truncate">{a.n}</span>
				<span class="text-app-dim text-xs whitespace-nowrap tabular-nums"
					>{matches.byDamage ? `damage ${a.dmg}` : a.t}</span
				>
			</div>
		{/each}
	</div>
	<div class="border-app-edge text-app-dim flex flex-wrap gap-x-4 border-t px-3.5 py-2 text-xs">
		<span>↑↓ to move</span><span>Enter to open</span><span>&gt; for commands</span>
	</div>
</div>
