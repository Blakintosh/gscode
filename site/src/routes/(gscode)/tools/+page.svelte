<script lang="ts">
	import DownloadIcon from '@lucide/svelte/icons/download';
	import RotateCcwIcon from '@lucide/svelte/icons/rotate-ccw';
	import { Button } from '$lib/components/ui/button';
	import DiscordIcon from '$lib/components/site/DiscordIcon.svelte';
	import GithubIcon from '$lib/components/site/GithubIcon.svelte';
	import SwapTree from '$lib/components/tools/SwapTree.svelte';
	import AssetPalette from '$lib/components/tools/AssetPalette.svelte';
	import BuildLog from '$lib/components/tools/BuildLog.svelte';
	import WorkshopPreflight from '$lib/components/tools/WorkshopPreflight.svelte';
	import SonarRace from '$lib/components/tools/SonarRace.svelte';
	import CompressionDemo from '$lib/components/tools/CompressionDemo.svelte';
	import ReplayButton from '$lib/components/tools/ReplayButton.svelte';
	import { reveal, reducedMotion } from '$lib/actions/reveal';
	import { discordInviteUrl, siteUrl } from '$lib/data/site';
	import { toolList, tools, versionChip } from '$lib/data/tools';

	// Each demo starts when it scrolls in; bumping its run count replays it.
	let live = $state({ swap: false, palette: false, flyby: false, log: false, preflight: false, race: false });
	let runs = $state({ swap: 0, palette: 0, log: 0, preflight: 0 });

	// Blackbird crosses its section's top edge once; its burn trail becomes the divider.
	let flown = $state(false);
	let flyInstant = $state(false);
	$effect(() => {
		if (!live.flyby) return;
		if (reducedMotion()) {
			flyInstant = true;
			flown = true;
			return;
		}
		const timer = setTimeout(() => (flown = true), 60);
		return () => clearTimeout(timer);
	});

	const title = 'Mod Tools: Apex, Blackbird, Ultrasound · gscode';
	const description =
		"Drop-in replacements for Black Ops III's asset editor, launcher and sound converter. Same files, same formats, none of the waiting.";

	const apexShots = {
		editor: { label: 'Weapon editor', src: '/images/tools/apex-weapon-editor.webp', alt: 'Apex editing a weapon' },
		compare: { label: 'Compare · Ctrl+D', src: '/images/tools/apex-compare.webp', alt: 'Apex comparing two weapons side by side' },
		table: { label: 'Open as table', src: '/images/tools/apex-table.webp', alt: 'Apex editing many assets in a table' }
	};
	type Shot = keyof typeof apexShots;
	let shot = $state<Shot>('editor');

	const apexDifferences = [
		['Ctrl+P', 'Open any asset by name, or by filter like <code>type:weapon</code>. Type <code>&gt;</code> for commands.'],
		['<200 ms', 'To open an asset. Arrowing through the list never stutters.'],
		['Crash-safe', 'Unsaved changes stay on disk through a crash or restart, until you save or discard them.'],
		['Compare · table', 'Ctrl+D lines assets up side by side. Open results as a table to edit many at once.'],
		['Inspector', 'What an asset uses, and what uses it.'],
		['Preview', "APE's own renderer, bit-exact. Plays xanims with their notetracks."],
		['Backups', 'Every save backs up the old GDT first, last 10 per file. A GDT that changed on disk is never overwritten without asking.'],
		['Extensions', "Add fields and sections to existing asset types. weapon-tech's recoil editor is the first."]
	];

	const blackbirdFeatures = [
		['Build', 'Compile, light and link with build presets. Errors and warnings are counted and filterable.'],
		['Run', 'Dev or Ship launch configs, offline or through Steam.'],
		['Projects', "New maps from Treyarch's templates. Rename or duplicate without leaving stale names in files."],
		['Checks', 'Finds a <code>#using</code> that points nowhere, and duplicate GDT assets.'],
		['Updates', 'In the background. "Update ready" appears in the title bar.']
	];

	const shortcuts: [string[], string][] = [
		[['Ctrl', 'K'], 'Command palette'],
		[['Ctrl', 'B'], 'Build'],
		[['F5'], 'Build and run'],
		[['Ctrl', 'Shift', 'U'], 'Publish to the Workshop'],
		[['Ctrl', 'Shift', 'A'], 'Asset Editor, which opens Apex'],
		[['Ctrl', 'Shift', 'R'], 'Radiant']
	];

	const formation = [
		{ tool: tools.blackbird, domain: 'air', file: 'modlauncher.exe' },
		{ tool: tools.apex, domain: 'land', file: 'asseteditor_modtools.exe' },
		{ tool: tools.ultrasound, domain: 'sea', file: 'snd_convert.exe' }
	];
</script>

<svelte:head>
	<title>{title}</title>
	<meta name="description" content={description} />
	<meta property="og:title" content="Mod Tools · gscode" />
	<meta property="og:site_name" content="gscode" />
	<meta property="og:description" content={description} />
	<meta property="og:image" content="{siteUrl}/og.png" />
	<meta property="og:url" content="{siteUrl}/tools" />
	<meta name="twitter:card" content="summary_large_image" />
</svelte:head>

{#snippet sectionLabel(no: string, name: string, tone: string, mark?: string)}
	<p class="type-label flex flex-wrap items-center gap-2.5 {tone}">
		{#if mark}<i class="marker {mark}"></i>{/if}{no} / {name}
	</p>
{/snippet}

{#snippet sectionHeading(text: string)}
	<h2 class="mt-4 max-w-[16ch] text-heading font-semibold tracking-heading text-balance">{text}</h2>
{/snippet}

{#snippet demoLabel(text: string, replay?: () => void)}
	<p class="type-label text-app-dim flex justify-between gap-3">
		<span>{text}</span>
		{#if replay}<ReplayButton onclick={replay} />{/if}
	</p>
{/snippet}

{#snippet richText(html: string)}
	<!-- Copy above is this file's own constants; only <code> is used for inline names. -->
	<!-- eslint-disable-next-line svelte/no-at-html-tags -->
	{@html html.replaceAll('<code>', '<span class="font-mono text-app-text">').replaceAll('</code>', '</span>')}
{/snippet}

<!-- ── Hero: the fleet in formation. One light source, top-right. ─────────────────── -->
<section class="bg-popover relative overflow-hidden" aria-labelledby="hero-title">
	<div class="lit-grid pointer-events-none absolute inset-0" aria-hidden="true"></div>
	<div class="lit-grid-glow pointer-events-none absolute inset-0" aria-hidden="true"></div>
	<div
		class="pointer-events-none absolute inset-0"
		aria-hidden="true"
		style="background:radial-gradient(44% 52% at 78% -6%, color-mix(in oklab, var(--bright) 20%, transparent), color-mix(in oklab, var(--violet) 9%, transparent) 52%, transparent 74%)"
	></div>
	<div
		class="light-enter pointer-events-none absolute top-0 bottom-0 left-[62%] w-[140px] [transform:skewX(-13deg)]"
		aria-hidden="true"
		style="background:linear-gradient(90deg, transparent, color-mix(in oklab, var(--bright) 5%, transparent) 44%, color-mix(in oklab, var(--bright) 17%, transparent) 50%, color-mix(in oklab, var(--violet) 8%, transparent) 58%, transparent);mask-image:linear-gradient(180deg, #000 0, #000 55%, transparent 100%)"
	></div>
	<i aria-hidden="true" class="border-primary bg-background absolute top-3 right-3 z-10 block size-[7px] border-[1.5px]"></i>
	<i aria-hidden="true" class="border-steel bg-background absolute bottom-3 left-3 z-10 block size-[7px] border-[1.5px]"></i>

	<div class="relative mx-auto max-w-7xl px-4 pt-20 pb-24 sm:px-6 lg:pt-28 lg:pb-32">
		<div class="text-primary pointer-events-none absolute top-8 left-4 font-mono text-2xs tracking-widest sm:left-6" aria-hidden="true">
			<i class="bg-primary absolute block h-px w-8"></i>
			<i class="bg-primary absolute block h-8 w-px"></i>
			<span class="absolute top-[-6px] left-9 whitespace-nowrap">0, 0</span>
		</div>

		<div class="grid grid-cols-[repeat(auto-fit,minmax(min(100%,440px),1fr))] items-center gap-10 lg:gap-14">
			<div class="min-w-0">
				<p class="type-label text-primary mb-7">Black Ops III Mod Tools</p>
				<h1 id="hero-title" class="font-display text-foreground max-w-[16ch] text-hero font-bold text-balance uppercase">
					The modder's tooling, <span class="grad-text">made better.</span>
				</h1>
				<p class="text-muted-foreground mt-7 max-w-[52ch] text-lg leading-relaxed font-light text-pretty sm:text-xl">
					Three drop-in replacements for Black Ops III's asset editor, launcher and sound converter. Same files, same
					formats, none of the waiting.
				</p>
				<div class="mt-9 flex flex-wrap items-center gap-3">
					<Button href="#install" size="lg"><DownloadIcon class="size-4" />Install all three</Button>
					<Button href="#apex" variant="secondary" size="lg">Meet the fleet</Button>
				</div>
			</div>

			<!-- Formation: air, land, sea. Below sm the three stack as a list. -->
			<div class="relative hidden aspect-[1/0.92] w-full min-w-0 sm:block" role="img" aria-label="Blackbird, Apex and Ultrasound in formation">
				<div aria-hidden="true" class="bg-border absolute inset-x-0 top-[69%] h-px"></div>
				<span aria-hidden="true" class="text-dim absolute top-[calc(69%+8px)] right-0 font-mono text-2xs tracking-[0.04em]">0 m</span>
				<img src={tools.blackbird.mascot} alt="" class="cast absolute top-[-2%] left-[52%] w-[40%]" />
				<img src={tools.apex.mascot} alt="" class="cast absolute top-[18%] left-0 w-[56%]" />
				<img src={tools.ultrasound.mascot} alt="" class="cast absolute top-[58%] left-[47%] w-[46%]" />
				{#each [{ f: formation[0], pos: 'left-[66%] top-[44%]' }, { f: formation[1], pos: 'left-[6%] top-[76%]' }, { f: formation[2], pos: 'right-0 top-[97%] items-end' }] as { f, pos } (f.domain)}
					<div class="absolute flex flex-col gap-1.5 {pos}">
						<span class="type-label text-foreground flex items-center gap-2"><i class="marker {f.tool.mark}"></i>{f.tool.name} · {f.domain}</span>
						<span class="text-dim font-mono text-2xs tracking-[0.04em] {pos.includes('items-end') ? '' : 'pl-3.5'}">{f.file}</span>
					</div>
				{/each}
			</div>
			<div class="border-border flex flex-col border-t sm:hidden">
				{#each formation as f (f.domain)}
					<div class="border-border grid grid-cols-[96px_minmax(0,1fr)] items-center gap-4 border-b py-4">
						<img src={f.tool.mascot} alt={f.tool.name} class="cast-sm w-24" />
						<div class="flex flex-col gap-1.5">
							<span class="type-label flex items-center gap-2"><i class="marker {f.tool.mark}"></i>{f.tool.name} · {f.domain}</span>
							<span class="text-dim font-mono text-2xs leading-[1.3] break-all">{f.file}</span>
						</div>
					</div>
				{/each}
			</div>
		</div>
	</div>
</section>

<!-- ── 00 The swap ─────────────────────────────────────────────────────────────── -->
<section class="border-border border-b" use:reveal={{ onIn: () => (live.swap = true), threshold: 0.3 }}>
	<div class="mx-auto flex max-w-7xl flex-wrap items-start gap-10 px-4 py-20 sm:px-6 lg:gap-14 lg:py-28">
		<div class="max-w-[440px] min-w-0 flex-[1_1_300px]">
			{@render sectionLabel('00', 'The swap', 'text-primary')}
			{@render sectionHeading('Same file names. Same shortcuts.')}
			<p class="text-muted-foreground mt-5 max-w-[46ch] text-body font-light text-pretty">
				Each setup puts its tool where Treyarch's was and moves the original into a hidden backup folder. Launch the Mod
				Tools exactly as before.
			</p>
			<Button variant="ghost" size="xs" class="mt-7" onclick={() => runs.swap++}>
				<RotateCcwIcon />Replay
			</Button>
		</div>
		<div class="min-w-0 flex-[2_1_520px]">
			<SwapTree active={live.swap} run={runs.swap} />
		</div>
	</div>
</section>

<!-- ── 01 Apex: the panther's eye ──────────────────────────────────────────────── -->
<section id="apex" class="bg-app-ground text-app-text border-app-edge relative scroll-mt-14 overflow-hidden border-b">
	<img
		src={tools.apex.mascot}
		alt=""
		aria-hidden="true"
		class="pointer-events-none absolute top-[-6%] right-[-14%] hidden w-[min(62vw,860px)] [filter:drop-shadow(-24px_28px_18px_var(--cast))] min-[1100px]:block"
	/>
	<div class="relative mx-auto max-w-7xl px-4 py-20 sm:px-6 lg:py-28">
		<div class="max-w-[520px] min-[1100px]:min-h-[380px]">
			<img src={tools.apex.mascot} alt="" aria-hidden="true" class="mb-7 block w-[clamp(140px,36vw,220px)] [filter:drop-shadow(-12px_14px_10px_var(--cast))] min-[1100px]:hidden" />
			{@render sectionLabel('01', 'Apex', 'text-eye-ink', 'bg-eye')}
			{@render sectionHeading('The asset editor that keeps up')}
			<p class="text-app-mute mt-5 max-w-[46ch] text-body font-light text-pretty">
				Apex replaces APE, the Asset Property Editor, and improves its stability, performance and quality of life. It
				edits the same GDTs: weapons, models, materials and tables. It lets you do everything APE does, faster and more
				easily.
			</p>
			<div class="mt-6 flex flex-wrap items-center gap-2.5">
				<span class="badge-cut bg-app-raise text-app-text type-label inline-flex h-6 items-center gap-1.5 px-3"
					><i class="marker bg-eye"></i>{versionChip(tools.apex)}</span
				>
				<span class="text-app-mute text-sm">Apex is in public preview.</span>
			</div>
		</div>

		<div class="mt-12 flex flex-wrap items-stretch gap-6">
			<div class="flex min-w-0 flex-[1_1_360px] flex-col gap-2.5" use:reveal={{ onIn: () => (live.palette = true), threshold: 0.3 }}>
				{@render demoLabel('Ctrl+P · go to asset', () => runs.palette++)}
				<div class="flex-1"><AssetPalette active={live.palette} run={runs.palette} /></div>
			</div>

			<div class="flex min-w-0 flex-[1.6_1_480px] flex-col gap-2.5">
				<div class="type-label flex flex-wrap items-center" role="tablist" aria-label="Apex screens">
					{#each Object.entries(apexShots) as [key, s] (key)}
						<button
							type="button"
							role="tab"
							aria-selected={shot === key}
							onclick={() => (shot = key as Shot)}
							class="mr-3 cursor-pointer pr-3 pb-2 outline-none focus-visible:text-app-text {shot === key
								? 'text-app-text shadow-[inset_0_-2px_0_var(--eye)]'
								: 'text-app-dim hover:text-app-text'}">{s.label}</button
						>
					{/each}
				</div>
				<div class="chamfer bg-app-edge p-px">
					<img
						src={apexShots[shot].src}
						alt={apexShots[shot].alt}
						width="1600"
						height="1000"
						class="chamfer bg-app-panel block aspect-[1600/1000] h-auto w-full object-cover object-top-left [--cut:14.5px]"
					/>
				</div>
			</div>
		</div>

		<div class="mt-14">
			<p class="type-label text-app-dim mb-4">What's different from APE</p>
			<div class="border-app-edge grid grid-cols-[repeat(auto-fit,minmax(min(100%,280px),1fr))] border-t">
				{#each apexDifferences as [name, copy] (name)}
					<div class="border-app-edge border-b pt-4 pr-5 pb-[18px]">
						<p class="type-label text-eye-ink">{name}</p>
						<p class="text-app-mute mt-2 text-sm">{@render richText(copy)}</p>
					</div>
				{/each}
			</div>
		</div>
	</div>
</section>

<!-- ── 02 Blackbird: the burn ──────────────────────────────────────────────────── -->
<section id="blackbird" class="bg-app-ground text-app-text border-app-edge relative scroll-mt-14 overflow-hidden border-b">
	<!-- The plane crosses on a diagonal; its afterburner trails become the divider. -->
	<div
		aria-hidden="true"
		class="border-app-edge pointer-events-none relative h-[150px] border-t"
		use:reveal={{ onIn: () => (live.flyby = true), threshold: 0.3 }}
	>
		<div class="absolute top-[92px] left-[clamp(16px,8vw,120px)] h-0 w-[120%] origin-left -rotate-[4deg]">
			{#each ['left-[124px] top-[28px]', 'left-[138px] -top-[24px]'] as pos (pos)}
				<div
					class="bg-burn absolute right-0 h-0.5 origin-right {pos} {flyInstant ? '' : 'transition-transform duration-[1400ms] ease-[cubic-bezier(0.25,0.8,0.25,1)]'}"
					style:scale="{flown ? 1 : 0} 1"
				></div>
			{/each}
			<img
				src={tools.blackbird.mascot}
				alt=""
				class="absolute -top-20 left-0 size-40 [filter:drop-shadow(-10px_14px_8px_var(--cast))] {flyInstant
					? ''
					: 'transition-transform duration-[1400ms] ease-[cubic-bezier(0.25,0.8,0.25,1)]'}"
				style:transform="translateX({flown ? '0px' : '110vw'}) rotate(-48deg)"
			/>
		</div>
	</div>

	<div class="relative mx-auto max-w-7xl px-4 pt-6 pb-20 sm:px-6 lg:pb-28">
		<div class="flex flex-wrap items-start gap-10 lg:gap-14">
			<div class="max-w-[420px] min-w-0 flex-[1_1_300px]">
				{@render sectionLabel('02', 'Blackbird', 'text-burn-ink', 'bg-burn')}
				{@render sectionHeading('The launcher, streamlined')}
				<p class="text-app-mute mt-5 max-w-[46ch] text-body font-light text-pretty">
					Blackbird is a new take on the Mod Tools Launcher. It's organised around your projects, remembers each one's
					settings, and shows only what you need to build, run and publish. Launch the Mod Tools from Steam as usual and
					it opens.
				</p>
				<dl class="border-app-edge mt-7 flex flex-col border-t">
					{#each blackbirdFeatures as [name, copy] (name)}
						<div class="border-app-edge grid grid-cols-[96px_minmax(0,1fr)] gap-4 border-b py-3">
							<dt class="type-label text-app-dim leading-normal">{name}</dt>
							<dd class="text-app-mute text-sm">{@render richText(copy)}</dd>
						</div>
					{/each}
				</dl>
			</div>

			<div class="flex min-w-0 flex-[2_1_520px] flex-col gap-2.5" use:reveal={{ onIn: () => (live.log = true), threshold: 0.3 }}>
				{@render demoLabel('Ctrl+B · build', () => runs.log++)}
				<BuildLog active={live.log} run={runs.log} />
			</div>
		</div>

		<div class="mt-14 flex flex-wrap items-start gap-6">
			<div class="min-w-0 flex-[1.4_1_420px]">
				<p class="type-label text-app-dim mb-4">A shortcut for everything</p>
				<div class="bg-app-edge border-app-edge grid grid-cols-[repeat(auto-fill,minmax(min(100%,190px),1fr))] gap-px border">
					{#each shortcuts as [keys, action] (action)}
						<div class="bg-app-panel flex flex-col gap-3 p-4">
							<span class="flex flex-wrap gap-1">
								{#each keys as key, i (key)}
									<kbd
										class="chamfer chamfer-xs inline-flex h-[30px] min-w-[30px] items-center justify-center px-[9px] font-mono text-xs {i ===
										keys.length - 1
											? 'bg-burn font-semibold text-[#1b1b1b]'
											: 'bg-app-raise text-app-text shadow-[inset_0_-2px_0_var(--app-edge)]'}">{key}</kbd
									>
								{/each}
							</span>
							<span class="text-app-mute text-sm leading-[1.4]">{action}</span>
						</div>
					{/each}
				</div>
			</div>

			<div class="min-w-0 flex-[1_1_320px]" use:reveal={{ onIn: () => (live.preflight = true), threshold: 0.3 }}>
				<div class="mb-4">{@render demoLabel('Workshop · preflight', () => runs.preflight++)}</div>
				<WorkshopPreflight active={live.preflight} run={runs.preflight} />
			</div>
		</div>
	</div>
</section>

<!-- ── 03 Ultrasound: the ping ─────────────────────────────────────────────────── -->
<section id="ultrasound" class="bg-app-ground text-app-text border-border relative scroll-mt-14 overflow-hidden border-b">
	<img
		src={tools.ultrasound.mascot}
		alt=""
		aria-hidden="true"
		class="pointer-events-none absolute top-6 right-[-4%] hidden w-[min(40vw,520px)] [filter:drop-shadow(-18px_22px_14px_var(--cast))] min-[1100px]:block"
	/>
	<div class="relative mx-auto max-w-7xl px-4 py-20 sm:px-6 lg:py-28">
		<div class="max-w-[520px] min-[1100px]:min-h-[300px]">
			<img src={tools.ultrasound.mascot} alt="" aria-hidden="true" class="mb-7 block w-[clamp(140px,36vw,220px)] [filter:drop-shadow(-12px_14px_10px_var(--cast))] min-[1100px]:hidden" />
			{@render sectionLabel('03', 'Ultrasound', 'text-sonar-ink', 'bg-sonar')}
			{@render sectionHeading('Less noise, more sound')}
			<p class="text-app-mute mt-5 max-w-[46ch] text-body font-light text-pretty">
				Ultrasound is a drop-in replacement for snd_convert, written in Rust. It's much faster, more reliable, and adds
				flexibility snd_convert never had.
			</p>
			<div class="mt-6 flex flex-wrap items-center gap-2.5">
				<span class="badge-cut bg-app-raise text-app-text type-label inline-flex h-6 items-center gap-1.5 px-3"
					><i class="marker bg-sonar"></i>{tools.ultrasound.status} · v{tools.ultrasound.version}</span
				>
				<span class="type-label text-app-dim">MIT licensed</span>
			</div>
		</div>

		<div class="mt-10" use:reveal={{ onIn: () => (live.race = true), threshold: 0.3 }}>
			<SonarRace active={live.race} />
		</div>

		<div class="mt-14"><CompressionDemo /></div>
	</div>
</section>

<!-- ── 04 Install ──────────────────────────────────────────────────────────────── -->
<section id="install" class="bg-popover relative scroll-mt-14 overflow-hidden">
	<div class="lit-grid pointer-events-none absolute inset-0 opacity-60" aria-hidden="true"></div>
	<div class="relative mx-auto max-w-7xl px-4 py-20 sm:px-6 lg:py-24">
		<div class="mb-10 flex flex-wrap items-end justify-between gap-5">
			<div>
				<p class="type-label text-primary">04 / Install <span class="text-dim">· 3 setups</span></p>
				<h2 class="font-display text-foreground mt-4 text-display font-bold uppercase">
					Install the <span class="grad-text">fleet</span>
				</h2>
			</div>
			<p class="type-label text-muted-foreground text-xs leading-[1.7]">Free and open source</p>
		</div>

		<div class="grid grid-cols-[repeat(auto-fit,minmax(min(100%,300px),1fr))] gap-5">
			{#each toolList as t (t.id)}
				<div class="rimmed rimmed-card chamfer flex flex-col gap-4 p-6">
					<div class="flex items-start justify-between gap-3">
						<img src={t.mascot} alt={t.name} class="cast-sm size-[84px]" />
						<span class="flex flex-col items-end gap-2.5">
							<span class="badge-cut bg-border inline-flex p-px">
								<span
									class="badge-cut bg-background text-foreground inline-flex h-5 items-center gap-1.5 px-2.5 font-mono text-2xs font-semibold tracking-[0.08em] uppercase [--cut:6.5px]"
									><i class="marker {t.mark}"></i>{versionChip(t)}</span
								>
							</span>
							<a
								href={t.repoUrl}
								target="_blank"
								rel="noopener noreferrer"
								aria-label="{t.name} on GitHub"
								class="type-label text-muted-foreground hover:text-primary inline-flex items-center gap-1.5"
								><GithubIcon class="size-3.5" />Source</a
							>
						</span>
					</div>
					<div>
						<h3 class="type-display text-2xl leading-none">{t.name}</h3>
						<p class="text-muted-foreground mt-2 text-sm">{t.job}</p>
					</div>
					<dl class="border-border border-t text-[13px] leading-normal">
						<div class="border-border grid grid-cols-[80px_minmax(0,1fr)] gap-3 border-b py-2">
							<dt class="type-label text-dim leading-[1.6]">Replaces</dt>
							<dd class="text-foreground font-mono [overflow-wrap:anywhere]">{t.replaces}</dd>
						</div>
						<div class="border-border grid grid-cols-[80px_minmax(0,1fr)] gap-3 border-b py-2">
							<dt class="type-label text-dim leading-[1.6]">Needs</dt>
							<dd class="text-muted-foreground">{t.needs}</dd>
						</div>
						<div class="border-border grid grid-cols-[80px_minmax(0,1fr)] gap-3 border-b py-2">
							<dt class="type-label text-dim leading-[1.6]">Licence</dt>
							<dd class="text-foreground font-mono">{t.licence}</dd>
						</div>
					</dl>
					<Button href={t.releasesUrl} target="_blank" rel="noopener noreferrer" class="mt-auto">
						<DownloadIcon class="size-4" />Download setup
					</Button>
				</div>
			{/each}
		</div>
		<p class="text-muted-foreground mt-6 flex max-w-[80ch] items-baseline gap-2.5 text-sm">
			<i class="marker bg-primary -translate-y-0.5"></i>
			<span
				><b class="text-foreground">Install Blackbird first.</b> Apex then installs alongside APE, and Blackbird's Asset
				Editor button opens it. Without Blackbird, Apex takes APE's place.</span
			>
		</p>
	</div>
</section>

<!-- ── 05 Feedback ─────────────────────────────────────────────────────────────── -->
<section class="border-border border-t">
	<div class="mx-auto flex max-w-7xl flex-wrap items-end justify-between gap-10 px-4 py-20 sm:px-6 lg:gap-14 lg:py-28">
		<div class="max-w-[640px] min-w-0 flex-[1_1_420px]">
			{@render sectionLabel('05', 'Feedback', 'text-primary')}
			<h2 class="mt-4 text-heading font-semibold tracking-heading text-balance">We'd love your feedback</h2>
			<p class="text-muted-foreground mt-5 max-w-[52ch] text-body font-light text-pretty">
				Tell us what slows you down and what you'd like next. Join the Discord for early updates and exclusive content on
				these tools, and on everything else we're building for Black Ops III modders.
			</p>
			<div class="mt-7 flex flex-wrap gap-x-6 gap-y-3">
				{#each toolList as t (t.id)}
					<a
						href={t.issuesUrl}
						target="_blank"
						rel="noopener noreferrer"
						class="type-label text-muted-foreground hover:text-primary inline-flex items-center gap-2"
						><i class="marker {t.mark}"></i>{t.name} issues ↗</a
					>
				{/each}
			</div>
		</div>
		<Button href={discordInviteUrl} target="_blank" rel="noopener noreferrer" size="lg">
			<DiscordIcon class="size-4" />Join the Discord
		</Button>
	</div>
</section>
