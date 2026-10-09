<script lang="ts">
	/**
	 * The strip above the home hero that introduces gscode tools. It sits on the apps'
	 * graphite, like their sections on /tools, so it reads as a door into that page.
	 */
	import ArrowRightIcon from '@lucide/svelte/icons/arrow-right';
	import { toolList } from '$lib/data/tools';
</script>

<a
	href="/tools"
	class="group bg-app-ground text-app-text border-app-edge relative block overflow-hidden border-b outline-none focus-visible:[box-shadow:inset_0_0_0_2px_var(--ring)]"
>
	<div class="mx-auto flex max-w-7xl flex-wrap items-center gap-x-6 gap-y-3 px-4 py-3.5 sm:px-6">
		<span class="flex shrink-0 items-center gap-2" aria-hidden="true">
			{#each toolList as t (t.id)}
				<img src={t.mascot} alt="" class="cast-sm size-9" />
			{/each}
		</span>
		<p class="min-w-0 flex-[1_1_320px] text-sm">
			<span class="type-label text-primary mr-2.5">New</span>
			<b class="font-medium">gscode tools</b>
			<span class="text-app-mute">
				· Apex, Blackbird and Ultrasound replace Black Ops III's asset editor, launcher and sound converter.
			</span>
		</p>
		<span
			class="type-label text-primary group-hover:text-bright inline-flex items-center gap-1.5 whitespace-nowrap transition-colors"
		>
			Meet the tools
			<ArrowRightIcon class="size-3.5 transition-transform group-hover:translate-x-0.5" />
		</span>
	</div>
</a>
