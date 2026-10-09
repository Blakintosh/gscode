<script lang="ts">
	/**
	 * The benchmark as a sonar race: each converter sends a ping that reaches the far wall
	 * when its banks are written. Played back faster than real time, with the ratio between
	 * the two exact.
	 */
	import { reducedMotion } from '$lib/actions/reveal';
	import ReplayButton from './ReplayButton.svelte';

	let { active = false }: { active?: boolean } = $props();

	const races = {
		full: { slow: 52.1, fast: 6.5, speed: 10, mult: '8×', label: 'full rebuild' },
		warm: { slow: 19.9, fast: 1.1, speed: 4, mult: '18×', label: 'no changes' }
	};
	type Mode = keyof typeof races;

	let mode = $state<Mode>('full');
	let run = $state(0);
	// Elapsed playback time, in seconds.
	let t = $state(0);

	$effect(() => {
		if (!active) return;
		void run;
		const r = races[mode];
		const end = r.slow / r.speed;
		if (reducedMotion()) {
			t = end;
			return;
		}
		t = 0;
		const t0 = performance.now();
		let raf = requestAnimationFrame(function tick(now) {
			t = Math.min((now - t0) / 1000, end);
			if (t < end) raf = requestAnimationFrame(tick);
		});
		return () => cancelAnimationFrame(raf);
	});

	const race = $derived(races[mode]);
	const simulated = $derived(t * race.speed);
	const lanes = $derived([
		{
			name: 'Ultrasound',
			p: Math.min(simulated / race.fast, 1),
			time: Math.min(simulated, race.fast),
			doneNote: `banks written · ${race.mult} faster`,
			stroke: 'var(--sonar)',
			value: 'text-sonar-ink',
			label: 'text-app-text'
		},
		{
			name: 'snd_convert',
			p: Math.min(simulated / race.slow, 1),
			time: Math.min(simulated, race.slow),
			doneNote: 'banks written',
			stroke: 'var(--app-mute)',
			value: 'text-app-mute',
			label: 'text-app-mute'
		}
	]);

	const pick = (m: Mode) => {
		mode = m;
		run++;
	};

	// Three arcs trail the ping front, fading; the source sits at the right edge.
	const arcs = (p: number) =>
		[0, 1, 2]
			.map((i) => ({ r: p * 600 - i * 24, w: [4, 3, 2][i], o: [1, 0.55, 0.25][i] }))
			.filter((a) => a.r >= 8)
			.map((a) => {
				const x = (626 - a.r * Math.cos(0.6)).toFixed(1);
				const dy = a.r * Math.sin(0.6);
				return { ...a, d: `M${x} ${(46 - dy).toFixed(1)}A${a.r} ${a.r} 0 0 0 ${x} ${(46 + dy).toFixed(1)}` };
			});

	const segment = (on: boolean) =>
		`cursor-pointer rounded px-3 py-[5px] ${on ? 'bg-app-panel text-app-text' : 'text-app-dim hover:text-app-text'}`;
</script>

<div>
	<div class="mb-3.5 flex flex-wrap items-center gap-x-5 gap-y-3">
		<p class="type-label text-app-dim">Sonar race · map with ~1 GB of sound</p>
		<span class="bg-app-raise font-app inline-flex rounded-md p-0.5 text-xs" role="group" aria-label="Build">
			<button type="button" class={segment(mode === 'full')} aria-pressed={mode === 'full'} onclick={() => pick('full')}
				>Full rebuild</button
			>
			<button type="button" class={segment(mode === 'warm')} aria-pressed={mode === 'warm'} onclick={() => pick('warm')}
				>No changes</button
			>
		</span>
		<ReplayButton onclick={() => run++} class="ml-auto">Ping again</ReplayButton>
	</div>
	<div class="border-app-edge bg-app-panel border">
		{#each lanes as lane, i (lane.name)}
			{@const done = lane.p >= 1}
			<div class="border-app-edge grid grid-cols-[minmax(0,1fr)_clamp(108px,18vw,200px)] {i === 0 ? 'border-b' : ''}">
				<div class="relative min-w-0 pt-3.5 pb-2.5 pl-3.5">
					<p class="type-label absolute top-3 left-3.5 {lane.label}">{lane.name}</p>
					<svg viewBox="0 0 640 96" preserveAspectRatio="none" aria-hidden="true" class="block h-[72px] w-full overflow-hidden sm:h-24">
						{#each { length: 11 } as _, k (k)}
							<line
								x1={20 + k * 60}
								x2={20 + k * 60}
								y1={k % 5 ? 86 : 80}
								y2="92"
								stroke="var(--app-edge)"
								vector-effect="non-scaling-stroke"
							/>
						{/each}
						<line x1="20" x2="620" y1="92" y2="92" stroke="var(--app-edge)" vector-effect="non-scaling-stroke" />
						<line
							x1="20"
							x2="20"
							y1="22"
							y2="92"
							stroke={done ? lane.stroke : 'var(--app-edge)'}
							stroke-width={done ? 3 : 1}
							stroke-dasharray={done ? undefined : '3 4'}
							vector-effect="non-scaling-stroke"
						/>
						<rect x="622" y="42" width="8" height="8" fill={lane.stroke} />
						{#each arcs(lane.p) as a, k (k)}
							<path
								d={a.d}
								fill="none"
								stroke={lane.stroke}
								stroke-width={a.w}
								opacity={a.o}
								vector-effect="non-scaling-stroke"
							/>
						{/each}
					</svg>
				</div>
				<div class="border-app-edge flex flex-col justify-center gap-1.5 border-l p-3.5">
					<b
						class="font-display text-[clamp(22px,3vw,32px)] leading-none font-bold tracking-[0.03em] tabular-nums {lane.value}"
						>{lane.time.toFixed(1)} s</b
					>
					<span class="type-label text-app-dim leading-[1.3]">{done ? lane.doneNote : 'converting'}</span>
				</div>
			</div>
		{/each}
	</div>
	<p class="text-app-mute mt-3 text-sm">
		Measured on a map with about 1 GB of sound, {race.label}: {race.slow} s → {race.fast} s. Played back {race.speed}×
		faster than real time; the ratio between the two is exact.
		<b class="text-app-text font-medium">Your mileage may vary.</b>
	</p>
</div>
