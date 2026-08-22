<script lang="ts">
	import MoonIcon from '@lucide/svelte/icons/moon';
	import SunIcon from '@lucide/svelte/icons/sun';
	import { toggleMode } from 'mode-watcher';
	import { Button } from '$lib/components/ui/button';
</script>

<!-- Sun/moon cross-fade only — nothing in Datum rotates. -->
<Button onclick={toggleMode} variant="ghost" size="icon" aria-label="Toggle theme" class="relative">
	<SunIcon
		class="size-4 scale-100 opacity-100 transition-[opacity,transform] duration-150 dark:scale-75 dark:opacity-0"
	/>
	<MoonIcon
		class="absolute size-4 scale-75 opacity-0 transition-[opacity,transform] duration-150 dark:scale-100 dark:opacity-100"
	/>
</Button>
