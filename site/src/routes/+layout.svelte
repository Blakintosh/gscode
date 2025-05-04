<script lang="ts">
	import '../css/app.css';
	import { ModeWatcher } from 'mode-watcher';
</script>

<ModeWatcher/>

<div class="app bg-background max-h-[100svh]">
	<div class="flex flex-col h-[100svh] w-full">
        <slot/>
	</div>
</div>