<script lang="ts">
	/**
	 * Blackbird's build view, after its main-build-warnings screen: the log streams line by
	 * line with the warning count rising, and the counts filter the log when clicked.
	 */
	import { reducedMotion } from '$lib/actions/reveal';

	let { active = false, run = 0 }: { active?: boolean; run?: number } = $props();

	type Kind = 'text' | 'cmd' | 'dim' | 'warn' | 'ok' | 'err';
	// [line, kind, ms after the previous line]
	const log: [string, Kind, number][] = [
		['Blackbird build · zm_castle_redux · Dev', 'text', 200],
		['> cod2map64.exe -platform pc -loadFrom map_source\\zm\\zm_castle_redux.map zm_castle_redux', 'cmd', 260],
		['Loading map source… 18,412 brushes, 2,106 entities', 'text', 420],
		['Compiling BSP (full)', 'dim', 360],
		['BSP compiled in 40.8 s', 'text', 1300],
		['> linker_modtools.exe -language english -modsource zm_castle_redux', 'cmd', 300],
		['Linking zone zm_castle_redux', 'text', 380],
		["WARNING: image 'i_castle_banner_c' is 4096x4096 and not streamed; consider enabling streaming", 'warn', 320],
		["WARNING: sound alias 'zmb_castle_bell_toll' has no subtitle", 'warn', 220],
		["WARNING: weapon 'ray_gun_castle' uses a deprecated fx 'wpn_ray_gun_trail'", 'warn', 220],
		['Wrote zone\\zm_castle_redux.ff (212.4 MB)', 'text', 320],
		['Link finished with 3 warnings', 'ok', 260],
		['', 'text', 120],
		['Build completed successfully.', 'dim', 240]
	];
	const colour: Record<Kind, string> = {
		text: 'text-app-text',
		cmd: 'text-app-cmd',
		dim: 'text-app-mute',
		warn: 'text-app-warn',
		ok: 'text-app-ok',
		err: 'text-app-err'
	};

	let shownCount = $state(0);
	let seconds = $state(0);
	let done = $state(false);
	let filter = $state<'all' | 'warn' | 'err'>('all');

	$effect(() => {
		if (!active) return;
		void run;
		filter = 'all';
		const total = log.reduce((t, l) => t + l[2], 0);
		if (reducedMotion()) {
			shownCount = log.length;
			seconds = Math.ceil(total / 1000);
			done = true;
			return;
		}
		shownCount = 0;
		seconds = 0;
		done = false;
		const timers: ReturnType<typeof setTimeout>[] = [];
		let t = 0;
		log.forEach((l, i) => {
			t += l[2];
			timers.push(setTimeout(() => (shownCount = i + 1), t));
		});
		for (let s = 1; s * 1000 < total; s++) timers.push(setTimeout(() => (seconds = s), s * 1000));
		timers.push(
			setTimeout(() => {
				done = true;
				seconds = Math.ceil(total / 1000);
			}, total + 200)
		);
		return () => timers.forEach(clearTimeout);
	});

	const shown = $derived(log.slice(0, shownCount).map(([text, kind], i) => ({ n: i + 1, text, kind })));
	const warnings = $derived(shown.filter((l) => l.kind === 'warn').length);
	const errors = $derived(shown.filter((l) => l.kind === 'err').length);
	const lines = $derived(filter === 'all' ? shown : shown.filter((l) => l.kind === filter));
	const status = $derived(
		done
			? `Build succeeded · ${warnings} warnings`
			: shownCount
				? `Building · 0:${String(seconds).padStart(2, '0')}`
				: 'Ready'
	);
	const progress = $derived(done ? 0 : Math.round((shownCount / log.length) * 100));

	const toggle = (f: 'warn' | 'err') => (filter = filter === f ? 'all' : f);
</script>

<div
	class="bg-app-ground border-app-edge font-app overflow-hidden rounded-lg border text-[13px] font-normal shadow-[0_18px_28px_rgba(0,0,0,.35)]"
>
	<div class="flex flex-wrap items-center gap-2.5 px-3 py-2.5">
		<span
			class="border-app-edge bg-app-panel inline-flex h-[30px] items-center gap-2 rounded-md border px-2.5 font-medium"
			>Castle Redux
			<span class="bg-app-raise text-app-mute rounded px-1.5 py-px text-[10px] font-medium">MAP</span></span
		>
		<span class="text-app-mute ml-auto text-xs whitespace-nowrap"
			>Launch config <b class="text-app-text font-medium">Dev · Offline</b></span
		>
		<span class="inline-flex h-[30px] items-center rounded-md bg-[var(--teal)] px-3.5 font-medium text-[var(--ink)]"
			>{done ? 'Build' : 'Cancel'}</span
		>
	</div>
	<div class="border-app-edge bg-app-panel mx-2 mb-2 rounded-[7px] border">
		<div class="border-app-edge flex flex-wrap items-center gap-4 border-b px-3.5 py-2.5 text-[12.5px]">
			<button
				type="button"
				onclick={() => toggle('err')}
				aria-pressed={filter === 'err'}
				class="text-app-mute hover:text-app-text inline-flex cursor-pointer items-center gap-1.5 rounded-[5px] px-2 py-[3px] {filter ===
				'err'
					? 'bg-app-raise'
					: ''}"><i class="marker bg-app-err"></i>{errors} errors</button
			>
			<button
				type="button"
				onclick={() => toggle('warn')}
				aria-pressed={filter === 'warn'}
				class="text-app-mute hover:text-app-text inline-flex cursor-pointer items-center gap-1.5 rounded-[5px] px-2 py-[3px] {filter ===
				'warn'
					? 'bg-app-raise'
					: ''}"><i class="marker bg-app-warn"></i>{warnings} warnings</button
			>
			<span class="text-app-dim ml-auto text-xs" aria-live="polite">
				{#if filter !== 'all'}Showing {filter === 'err' ? 'errors' : 'warnings'} only · click again to clear{/if}
			</span>
		</div>
		<div class="font-app-mono min-h-[300px] overflow-x-auto py-2.5 text-[12.5px] leading-[1.55]">
			{#each lines as l (l.n)}
				<div class="grid grid-cols-[40px_minmax(0,1fr)] gap-2 pr-3">
					<span class="text-app-dim text-right tabular-nums">{l.n}</span>
					<span class="{colour[l.kind]} whitespace-pre-wrap [overflow-wrap:anywhere]">{l.text || ' '}</span>
				</div>
			{/each}
		</div>
		<div class="border-app-edge text-app-mute relative flex items-center gap-2 border-t px-3.5 py-[9px] text-[12.5px]">
			<i class="bg-app-cmd absolute -top-px left-0 h-0.5 transition-[width] duration-200 ease-linear" style:width="{progress}%"
			></i>
			<span>{status}</span>
		</div>
	</div>
</div>
