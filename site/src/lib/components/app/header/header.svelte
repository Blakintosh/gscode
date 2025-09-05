<script lang="ts">
	import { page } from '$app/stores';
	import FontSelector from '$components/app/font-selector/font-selector.svelte';
	import ThemeSelector from '$components/app/theme-selector/theme-selector.svelte';
	import { Button } from "$lib/components/ui/button/index.js";
	import * as Popover from "$lib/components/ui/popover/index.js";
	import * as Sidebar from "$lib/components/ui/sidebar/index.js";
	import { Separator } from "$lib/components/ui/separator/index.js";

	// @ts-ignore
	import Download from "lucide-svelte/icons/download";
	// @ts-ignore
	import Menu from "lucide-svelte/icons/menu";
	import { MediaQuery } from 'svelte/reactivity';

	let pageTitle: string = $derived.by(() => {
		switch($page.url.pathname) {
			case "/":
				return "Home";
			case "/library":
				return "Script API Reference";
		}
		return "";
	});

	const navigation = [
		{
			label: "Home",
			href: "/"
		},
		{
			label: "Script API Reference",
			href: "/library"
		}
	];

	let isDesktop = new MediaQuery("(min-width: 768px)");

	let popoverOpen = $state(false);
</script>

<header class="relative flex justify-between items-center w-full py-2 px-6 bg-sidebar border-sidebar-border border-b z-10">
	{#if isDesktop.current}
		<div class="flex items-center justify-start gap-2 w-[20vw]">
			<a href="/" aria-label="GSCode Logo">
				<div class="h-10 w-20 bg-gscodeLight dark:bg-gscode bg-cover" role="img">
				</div>
			</a>
		</div>

		<div class="flex items-center justify-center grow gap-4">
			{#each navigation as item}
				<Button href={item.href} variant={"ghost"}>
					{item.label}
				</Button>
			{/each}
		</div>

		<div class="flex items-center justify-end gap-2 w-[20vw]">
			<FontSelector/>
			<ThemeSelector/>

			<Button size="sm" class="gap-2" href="https://marketplace.visualstudio.com/items?itemName=blakintosh.gscode" target="_blank" rel="noopener noreferrer">
				<Download class="w-4 h-4"/>
				v0.3.0-beta
			</Button>
		</div>
	{:else}	
		<a href="/" aria-label="GSCode Logo">
			<div class="h-10 w-20 bg-gscodeLight dark:bg-gscode bg-cover" role="img">
			</div>
		</a>
		<Popover.Root bind:open={popoverOpen}>
			<Popover.Trigger>
				{#snippet child({ props })}
					<Button size="icon" variant={"ghost"} {...props}>
						<Menu class="w-4 h-4"/>
					</Button>
				{/snippet}
			</Popover.Trigger>
			<Popover.Content class="space-y-4 w-64">
				<Sidebar.Root collapsible={"none"}>
					<Sidebar.Content class="bg-background">
						<Sidebar.Menu>
							{#each navigation as item}
								<Sidebar.MenuItem>
									<Sidebar.MenuButton>
										{#snippet child({ props })}
											<Button href={item.href} variant={"ghost"} {...props} class="justify-start w-full" onclick={() => (popoverOpen = false)}>
												{item.label}
											</Button>
										{/snippet}
									</Sidebar.MenuButton>
								</Sidebar.MenuItem>
							{/each}
						</Sidebar.Menu>
					</Sidebar.Content>
				</Sidebar.Root>

				<Separator orientation="horizontal"/>
				
				<div class="flex items-center gap-2">
					<Button size="icon" href="https://marketplace.visualstudio.com/items?itemName=blakintosh.gscode" target="_blank" rel="noopener noreferrer">

						<Download class="w-4 h-4"/>
					</Button>
					<FontSelector/>
					<ThemeSelector/>
				</div>
			</Popover.Content>
		</Popover.Root>
	{/if}
</header>