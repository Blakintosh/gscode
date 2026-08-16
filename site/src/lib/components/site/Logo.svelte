<script lang="ts">
	import { cn } from '$lib/utils.js';
	import DatumMark from './DatumMark.svelte';

	let {
		class: className = '',
		/** Optional property name rendered after the wordmark, e.g. "library". */
		property,
		href = '/'
	}: { class?: string; property?: string; href?: string } = $props();
</script>

<!-- Wordmark: mark · GSCODE (Chakra Petch 700, uppercase) · optional property (Sora 400, mute). -->
<a
	{href}
	class={cn(
		'group/logo flex items-center gap-[9px] outline-none focus-visible:[box-shadow:inset_0_-2px_0_var(--ring)]',
		className
	)}
	aria-label="gscode{property ? ` ${property}` : ''} — home"
>
	<DatumMark class="size-[17px]" />
	<span
		class="font-display text-[15px] leading-none font-bold tracking-[.04em] uppercase transition-colors group-hover/logo:text-primary"
	>
		gscode
	</span>
	{#if property}
		<span
			class="font-sans text-[13px] leading-none font-normal normal-case tracking-normal text-muted-foreground transition-colors group-hover/logo:text-foreground"
		>
			{property}
		</span>
	{/if}
</a>
