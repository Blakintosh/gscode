<script lang="ts">
	import { page } from '$app/state';
	import ArrowLeftIcon from '@lucide/svelte/icons/arrow-left';
	import Brush from '$lib/components/site/Brush.svelte';
	import Logo from '$lib/components/site/Logo.svelte';
	import { Button } from '$lib/components/ui/button';

	/** SvelteKit's own fallbacks say nothing useful — swap them for plain English. */
	const generic = ['Not Found', 'Internal Error', 'Internal Server Error'];

	const message = $derived.by(() => {
		const thrown = page.error?.message;
		if (thrown && !generic.includes(thrown)) return thrown;
		if (page.status === 404) return "That page doesn't exist.";
		return 'Something went wrong on our end.';
	});
</script>

<svelte:head>
	<title>{page.status} - GSCode</title>
</svelte:head>

<header class="bg-popover border-border border-b">
	<div class="mx-auto flex h-14 max-w-7xl items-center px-4 sm:px-6"><Logo /></div>
</header>
<div class="flex w-full grow items-center justify-center px-4 py-20 sm:px-6">
	<Brush
		surface="card"
		rim="deep"
		handles
		tab="error"
		readout={String(page.status)}
		class="w-full max-w-xl"
		bodyClass="px-6 pt-10 pb-7 sm:px-10 sm:pt-12 sm:pb-9"
	>
		<p class="type-display text-foreground text-6xl sm:text-7xl">{page.status}</p>
		<p class="text-muted-foreground mt-4 max-w-[52ch] text-[16.5px] font-light">
			{message}
		</p>
		<div class="mt-8 flex flex-wrap items-center gap-2">
			<Button href="/" size="sm">
				<ArrowLeftIcon />
				Back home
			</Button>
			<Button href="/library" variant="secondary" size="sm">Script API reference</Button>
		</div>
	</Brush>
</div>
