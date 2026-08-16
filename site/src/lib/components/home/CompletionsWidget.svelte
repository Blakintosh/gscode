<script lang="ts">
	/**
	 * The completions story: `util::d` types itself and the list narrows on every
	 * keystroke — namespace resolved, signatures attached. Rendered by hand (not through
	 * the highlighter) so the typed line can change cheaply.
	 */
	import Brush from '$lib/components/site/Brush.svelte';
	import { reducedMotion } from '$lib/actions/reveal';

	let { active = false }: { active?: boolean } = $props();

	const TYPED = 'util::de';
	const candidates = [
		{ name: 'damage_notify_wrapper', sig: '( damage, attacker, … )' },
		{ name: 'death_notify_wrapper', sig: '( attacker, damageType )' },
		{ name: 'debug_line', sig: '( start, end, … )' },
		{ name: 'delay_thread', sig: '( delay, func, … )' },
		{ name: 'delete_on_death', sig: '( ent )' },
		{ name: 'get_players', sig: '( team )' },
		{ name: 'waittill_any', sig: '( … )' }
	];

	let typed = $state('');
	$effect(() => {
		if (!active) return;
		if (reducedMotion()) {
			typed = TYPED;
			return;
		}
		const timers: ReturnType<typeof setTimeout>[] = [];
		for (let i = 1; i <= TYPED.length; i++) {
			timers.push(setTimeout(() => (typed = TYPED.slice(0, i)), 300 + i * 110));
		}
		return () => timers.forEach(clearTimeout);
	});

	const query = $derived(typed.includes('::') ? typed.split('::')[1] : '');
	const open = $derived(typed.includes('::'));
	const matches = $derived(candidates.filter((c) => c.name.startsWith(query)));
</script>

<div class="relative">
	<Brush surface="table" behind="background" handles readout="GSC" class="my-0" bodyClass="flex flex-col">
		<div class="bg-popover border-border border-b py-2 pr-4 pl-[36px] font-mono text-[10px] tracking-[.04em]">
			<span class="text-primary border-primary -mb-px border-b-2 py-2">_init.gsc</span>
		</div>
		<pre class="m-0 overflow-x-auto px-5 py-4 font-mono text-[13px] leading-[1.6]"><span class="text-[var(--code-directive)]">#using</span> scripts\shared\util_shared;

<span class="text-primary">function</span> <span class="text-bright">init</span>()
&#123;
    <span class="text-foreground">{typed}</span><span class="caret bg-primary ml-px inline-block h-[1.1em] w-[1.5px] translate-y-[3px]" aria-hidden="true"></span>
&#125;</pre>
	</Brush>

	<div
		class="reveal relative z-10 mt-2 ml-8 max-w-[520px] sm:ml-[132px]"
		data-in={open ? '' : undefined}
		aria-hidden={open ? undefined : 'true'}
	>
		<Brush surface="popover" behind="background" cut={10} rim="edge" shadow="overlay" bodyClass="py-1.5">
			{#each matches.slice(0, 4) as m, i (m.name)}
				<div
					class="flex items-center gap-3 border-l-2 px-3.5 py-1.5 font-mono text-[12.5px] transition-colors {i === 0
						? 'border-primary bg-[var(--wash-active)]'
						: 'border-transparent'}"
				>
					<span class="chip-cut bg-deep text-bright grid size-4 shrink-0 place-items-center text-[9px] font-medium">f</span>
					<span class="text-foreground">{m.name}</span>
					<span class="text-dim ml-auto truncate text-[11px]">{m.sig}</span>
				</div>
			{/each}
			{#if matches.length > 4}
				<div class="text-dim px-3.5 py-1 font-mono text-[11px]">… {matches.length - 4} more</div>
			{/if}
		</Brush>
	</div>
</div>

<style>
	pre {
		--code-directive: #16a899;
		color: var(--muted-foreground);
	}
	:global(html.light) pre,
	:global(html:not(.dark)) pre {
		--code-directive: #0b6b60;
	}
	.caret {
		animation: caret 1s steps(1) infinite;
	}
	@keyframes caret {
		50% {
			opacity: 0;
		}
	}
	@media (prefers-reduced-motion: reduce) {
		.caret {
			animation: none;
		}
	}
</style>
