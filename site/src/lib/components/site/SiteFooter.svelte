<script lang="ts">
	import {
		assetplaceUrl,
		discordInviteUrl,
		githubUrl,
		marketplaceUrl,
		wikiUrl
	} from '$lib/data/site';
	import DiscordIcon from './DiscordIcon.svelte';
	import GithubIcon from './GithubIcon.svelte';
	import Logo from './Logo.svelte';

	const ext = (href: string) => ({ href, target: '_blank', rel: 'noopener noreferrer' });
	const link = 'hover:text-foreground transition-colors';
</script>

<!-- Footer sits back on ground with a top edge — the page ends, it does not lift. -->
<footer class="border-border bg-background mt-16 border-t">
	<div class="mx-auto grid max-w-7xl gap-10 px-4 py-12 sm:px-6 md:grid-cols-4">
		<div class="md:col-span-2">
			<Logo />
			<p class="text-muted-foreground mt-4 max-w-sm text-sm">
				A language server for Black Ops III's GSC and CSC. It resolves your whole mod folder,
				then tells you what the Linker would reject — before you build.
			</p>
			<div class="mt-5 flex items-center gap-5">
				<a
					{...ext(discordInviteUrl)}
					class="text-muted-foreground {link} inline-flex items-center gap-2 text-sm"
				>
					<DiscordIcon class="size-4" />
					Join us on Discord
				</a>
				<a
					{...ext(githubUrl)}
					class="text-muted-foreground {link} inline-flex items-center gap-2 text-sm"
				>
					<GithubIcon class="size-4" />
					GitHub
				</a>
			</div>
		</div>

		<div>
			<h3 class="type-label text-primary mb-3 tracking-[.17em]">gscode</h3>
			<ul class="text-muted-foreground space-y-2 text-sm">
				<li><a {...ext(marketplaceUrl)} class={link}>Install for VS Code</a></li>
				<li><a href="/library" class={link}>Function library</a></li>
				<li><a href="/contributing" class={link}>Contributing</a></li>
			</ul>
		</div>

		<div>
			<h3 class="type-label text-primary mb-3 tracking-[.17em]">Ecosystem</h3>
			<ul class="text-muted-foreground space-y-2 text-sm">
				<li><a {...ext(wikiUrl)} class={link}>Wiki</a></li>
				<li><a {...ext(assetplaceUrl)} class={link}>Assetplace</a></li>
				<li><a {...ext(discordInviteUrl)} class={link}>Discord</a></li>
			</ul>
		</div>
	</div>
	<!-- Designator line: one line, mono, uppercased in CSS. -->
	<div class="border-border border-t">
		<p
			class="text-dim mx-auto max-w-7xl px-4 py-5 font-mono text-[10px] tracking-[.14em] uppercase sm:px-6"
		>
			gscode · GPL-3.0 · a community project · not affiliated with Activision or Treyarch
		</p>
	</div>
</footer>
