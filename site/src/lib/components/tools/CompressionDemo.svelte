<script lang="ts">
	/**
	 * Ultrasound's optional compression: drag from None to Extreme and the waveform thins,
	 * the bank size drops and the SZC line beside it changes to match.
	 */
	const levels = [
		{ label: 'None', key: 'none', pct: 100 },
		{ label: 'Low', key: 'low', pct: 74 },
		{ label: 'Medium', key: 'medium', pct: 52 },
		{ label: 'High', key: 'high', pct: 36 },
		{ label: 'Extreme', key: 'extreme', pct: 24 }
	];
	// A fixed, sound-like envelope: the same shape every render.
	const bars = Array.from(
		{ length: 48 },
		(_, i) => Math.round(18 + 78 * Math.abs(Math.sin(i * 0.47) * Math.cos(i * 0.13) + 0.35 * Math.sin(i * 1.9))) % 100 || 22
	);

	let index = $state(2);
	const level = $derived(levels[index]);
	const barWidth = $derived((1 + 5 * (level.pct / 100)).toFixed(2));
</script>

<div class="flex flex-wrap items-stretch gap-6">
	<div class="border-app-edge bg-app-panel min-w-0 flex-[1.6_1_460px] border p-5">
		<div class="flex flex-wrap items-end justify-between gap-3">
			<div>
				<p class="type-label text-app-dim">Optional compression · bank size</p>
				<b class="font-display mt-2.5 block text-[clamp(32px,4vw,44px)] leading-none font-bold tracking-[0.03em] tabular-nums"
					>{level.pct === 100 ? '100%' : `~${level.pct}%`}</b
				>
			</div>
			<span class="text-app-mute font-mono text-xs"
				>{level.pct === 100 ? 'of original · no compression' : `of original · ${level.key} · ymmv`}</span
			>
		</div>
		<div aria-hidden="true" class="mt-5 flex h-[120px] items-center">
			{#each bars as h, i (i)}
				<div class="flex h-full flex-1 items-center justify-center">
					<div class="bg-sonar transition-[width] duration-200" style:width="{barWidth}px" style:height="{Math.max(10, h)}%"></div>
				</div>
			{/each}
		</div>
		<div class="bg-app-raise mt-3 h-1">
			<div class="bg-sonar h-full transition-[width] duration-200" style:width="{level.pct}%"></div>
		</div>
		<div class="relative mt-5 h-9 has-[input:focus-visible]:[box-shadow:inset_0_0_0_1px_var(--ring)]">
			<div class="bg-app-raise absolute top-4 right-[10%] left-[10%] h-0.5">
				<div class="bg-sonar-ink h-full transition-[width] duration-200" style:width="{(index / 4) * 100}%"></div>
			</div>
			<div class="pointer-events-none absolute inset-0 grid grid-cols-5">
				{#each levels as l, i (l.key)}
					<div class="flex justify-center">
						<i class="mt-3 block size-2.5 transition-colors duration-200 {i <= index ? 'bg-sonar' : 'bg-app-edge'}"></i>
					</div>
				{/each}
			</div>
			<input
				type="range"
				min="0"
				max="4"
				step="1"
				bind:value={index}
				aria-label="Compression level"
				aria-valuetext={level.label}
				class="absolute inset-0 m-0 size-full cursor-pointer opacity-0"
			/>
		</div>
		<div class="grid grid-cols-5 text-center font-mono text-2xs font-semibold tracking-[0.08em] uppercase">
			{#each levels as l, i (l.key)}
				<button
					type="button"
					onclick={() => (index = i)}
					class="cursor-pointer py-1 {i === index ? 'text-app-text' : 'text-app-dim hover:text-app-text'}">{l.label}</button
				>
			{/each}
		</div>
	</div>
	<div class="flex min-w-0 flex-[1_1_300px] flex-col gap-4">
		<div class="border-app-edge bg-recess overflow-x-auto border p-4 font-mono text-[13px] leading-[1.7]">
			<p class="type-label text-app-dim mb-2">zm_castle_redux.szc</p>
			<div class="whitespace-nowrap">
				<span class="text-app-mute">"DefaultAliasCompression"</span><span class="text-app-dim">: </span><span
					class="text-sonar-ink">"{level.key}"</span
				>
			</div>
			<p class="text-app-mute mt-2.5 font-sans text-[13px] leading-normal font-light whitespace-normal">
				Set it for the whole project in the SZC, or per alias with the <span class="text-app-text font-mono"
					>CompressionLevel</span
				> column.
			</p>
		</div>
		<dl class="border-app-edge flex flex-col border-t">
			{#each [['Formats', 'WAV at any sample rate, correctly resampled. FLAC and OGG directly.'], ['Fixes', 'Long-standing snd_convert bugs.'], ['Updates', "After a successful build, verified against the release's SHA-256. Your build never waits for it."]] as [k, v] (k)}
				<div class="border-app-edge grid grid-cols-[88px_minmax(0,1fr)] gap-3.5 border-b py-[11px]">
					<dt class="type-label text-app-dim leading-normal">{k}</dt>
					<dd class="text-app-mute text-sm">{v}</dd>
				</div>
			{/each}
		</dl>
	</div>
</div>
