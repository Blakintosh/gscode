<script lang="ts">
	import ArrowUpRightIcon from '@lucide/svelte/icons/arrow-up-right';
	import DownloadIcon from '@lucide/svelte/icons/download';
	import MenuIcon from '@lucide/svelte/icons/menu';
	import { page } from '$app/state';
	import { cn } from '$lib/utils.js';
	import { Button } from '$lib/components/ui/button';
	import * as Sheet from '$lib/components/ui/sheet';
	import {
		assetplaceUrl,
		discordInviteUrl,
		extensionVersion,
		githubUrl,
		marketplaceUrl,
		wikiUrl
	} from '$lib/data/site';
	import DiscordIcon from './DiscordIcon.svelte';
	import GithubIcon from './GithubIcon.svelte';
	import Logo from './Logo.svelte';
	import ThemeToggle from './ThemeToggle.svelte';

	let mobileOpen = $state(false);

	/** Where the site is inside the ecosystem: the wordmark carries the property name. */
	const property = $derived(
		page.url.pathname.startsWith('/library')
			? 'library'
			: page.url.pathname.startsWith('/editor')
				? 'editor'
				: undefined
	);

	const navLinks = [
		{ href: '/', label: 'Home', exact: true },
		{ href: '/library', label: 'Library' }
	];

	/** The rest of the ecosystem — same server, same look, different jobs. */
	const ecosystemLinks = [
		{ href: wikiUrl, label: 'Wiki' },
		{ href: assetplaceUrl, label: 'Assetplace' }
	];

	function isActive(link: { href: string; exact?: boolean }): boolean {
		if (link.exact) return page.url.pathname === link.href;
		return page.url.pathname === link.href || page.url.pathname.startsWith(link.href + '/');
	}

	const navItem =
		'px-3 py-1.5 font-mono text-xs font-semibold tracking-label uppercase transition-colors outline-none focus-visible:text-primary';
	const sheetItem =
		'flex items-center gap-3 px-4 py-2.5 font-mono text-xs font-semibold tracking-label uppercase text-muted-foreground transition-colors hover:text-foreground hover:bg-[var(--wash-hover)] outline-none focus-visible:text-primary';
</script>

<!-- Raise-coloured bar on an edge; nav is mono uppercase, mute → teal. -->
<!-- The edge is an inset shadow so the bar is exactly h-14: layouts subtract 3.5rem for it. -->
<header class="bg-popover sticky top-0 z-50 shadow-[inset_0_-1px_0_var(--border)]">
	<!-- App surfaces (library, editor) are edge-to-edge below, so their chrome is too;
	 the marketing pages keep the contained column. -->
	<div
		class={cn(
			'flex h-14 items-center gap-3 px-4 sm:gap-5 sm:px-6',
			!property && 'mx-auto max-w-7xl'
		)}
	>
		<Logo {property} />

		<nav class="hidden items-center md:flex" aria-label="Primary">
			{#each navLinks as link (link.href)}
				<a
					href={link.href}
					aria-current={isActive(link) ? 'page' : undefined}
					class={cn(
						navItem,
						isActive(link) ? 'text-primary' : 'text-muted-foreground hover:text-primary'
					)}
				>
					{link.label}
				</a>
			{/each}
			<span aria-hidden="true" class="bg-border mx-2 h-4 w-px"></span>
			{#each ecosystemLinks as link (link.href)}
				<a
					href={link.href}
					target="_blank"
					rel="noopener noreferrer"
					class={cn(
						navItem,
						'text-muted-foreground hover:text-primary inline-flex items-center gap-0.5'
					)}
				>
					{link.label}
					<ArrowUpRightIcon class="text-dim size-3" />
				</a>
			{/each}
		</nav>

		<div class="ml-auto flex items-center gap-1.5">
			<Button
				href={discordInviteUrl}
				target="_blank"
				rel="noopener noreferrer"
				variant="ghost"
				size="icon"
				aria-label="Join us on Discord"
				class="hidden sm:inline-flex"
			>
				<DiscordIcon class="size-4.5" />
			</Button>
			<Button
				href={githubUrl}
				target="_blank"
				rel="noopener noreferrer"
				variant="ghost"
				size="icon"
				aria-label="GSCode on GitHub"
				class="hidden sm:inline-flex"
			>
				<GithubIcon class="size-4.5" />
			</Button>
			<ThemeToggle />
			<Button
				href={marketplaceUrl}
				target="_blank"
				rel="noopener noreferrer"
				size="sm"
				class="hidden sm:inline-flex"
			>
				<DownloadIcon class="size-3.5" />
				Install
				<span class="type-data text-2xs tracking-wider">
					v{extensionVersion}
				</span>
			</Button>

			<Sheet.Root bind:open={mobileOpen}>
				<Sheet.Trigger class="md:hidden">
					{#snippet child({ props })}
						<Button variant="ghost" size="icon" aria-label="Open menu" {...props}>
							<MenuIcon class="size-5" />
						</Button>
					{/snippet}
				</Sheet.Trigger>
				<Sheet.Content side="right" class="w-72 overflow-y-auto">
					<Sheet.Header>
						<Sheet.Title>Menu</Sheet.Title>
					</Sheet.Header>
					<nav class="flex flex-col pb-6" aria-label="Mobile">
						{#each navLinks as link (link.href)}
							<a
								href={link.href}
								class={cn(sheetItem, isActive(link) && 'text-primary')}
								onclick={() => (mobileOpen = false)}
							>
								{link.label}
							</a>
						{/each}
						<p class="type-label text-dim px-4 pt-4 pb-1">Ecosystem</p>
						{#each ecosystemLinks as link (link.href)}
							<a
								href={link.href}
								target="_blank"
								rel="noopener noreferrer"
								class={sheetItem}
								onclick={() => (mobileOpen = false)}
							>
								{link.label}
								<ArrowUpRightIcon class="text-dim size-3" />
							</a>
						{/each}
						<a
							href={discordInviteUrl}
							target="_blank"
							rel="noopener noreferrer"
							class={sheetItem}
							onclick={() => (mobileOpen = false)}
						>
							<DiscordIcon class="size-4" />
							Discord
						</a>
						<a
							href={githubUrl}
							target="_blank"
							rel="noopener noreferrer"
							class={sheetItem}
							onclick={() => (mobileOpen = false)}
						>
							<GithubIcon class="size-4" />
							GitHub
						</a>
						<div class="px-4 pt-4">
							<Button
								href={marketplaceUrl}
								target="_blank"
								rel="noopener noreferrer"
								size="sm"
								class="w-full"
							>
								<DownloadIcon class="size-3.5" />
								Install for VS Code
								<span class="type-data text-2xs tracking-wider">
									v{extensionVersion}
								</span>
							</Button>
						</div>
					</nav>
				</Sheet.Content>
			</Sheet.Root>
		</div>
	</div>
</header>
