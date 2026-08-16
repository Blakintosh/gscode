<script lang="ts">
	import '../css/app.css';
	import { ModeWatcher } from 'mode-watcher';
	import type { Snippet } from 'svelte';

	let { children }: { children: Snippet } = $props();
</script>

<ModeWatcher />

<div class="bg-background text-foreground flex min-h-svh w-full flex-col">
	{@render children()}
</div>
