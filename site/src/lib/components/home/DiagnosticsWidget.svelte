<script lang="ts">
	/**
	 * The diagnostics story: a GSC file whose three problems are underlined one after
	 * another as the widget scrolls into view, each error row landing just after its
	 * squiggle. The underlines are shaku annotations in the code block; their staggered
	 * reveal is CSS keyed off `data-in` on this element.
	 */
	import * as Code from '$lib/components/ui/code';
	import { reducedMotion } from '$lib/actions/reveal';

	let { active = false }: { active?: boolean } = $props();

	const source = `#insert scripts\\shared\\shrd.gsh;
//      ~~~~~~~~~~~~~~~~~~~~~~~~
function write_some_code( weapon_name )
{
    w_weapon = GetWeapon( weapon_name );
    ammo = w_weapon.clipsize * "2";
//                           ~~~~~
    current_health = self.health;

    if ( current_health > 20 )
    {
        self.health = current_health * 0.8;
    }

    return ammo
//             ~
}`;

	const errors = [
		{ line: 1, code: 'GSC1010', text: "Unable to locate file 'scripts\\shared\\shrd.gsh' for insert directive." },
		{ line: 5, code: 'GSC3021', text: "The operator '*' is not supported on types 'int' and 'string'." },
		{ line: 11, code: 'GSC2004', text: "';' expected to end return statement." }
	];

	// Number of error rows shown so far; steps up in time with the underlines.
	let shown = $state(0);
	$effect(() => {
		if (!active) return;
		if (reducedMotion()) {
			shown = errors.length;
			return;
		}
		const timers = errors.map((_, i) => setTimeout(() => (shown = i + 1), 350 + i * 420));
		return () => timers.forEach(clearTimeout);
	});
</script>

<div class="diagnostics-widget grid grid-cols-1 gap-5 lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]" data-in={active ? '' : undefined}>
	<Code.Root value="1" language="GSC" class="my-0" behind="background">
		<Code.Tabs><Code.Tab value="1">_weapon_utils.gsc</Code.Tab></Code.Tabs>
		<Code.Example value="1"><Code.Block code={source} /></Code.Example>
	</Code.Root>

	<!-- The Problems panel: mono rows on the recess, 2px Clip rule — error is the one meaning Clip carries. -->
	<div class="flex flex-col gap-2 self-start">
		<p class="type-label text-dim flex items-center justify-between tracking-[.18em]">
			<span>Problems</span>
			<span class="text-destructive tabular-nums">{shown} err</span>
		</p>
		{#each errors as err, i (err.code)}
			<div
				class="reveal bg-recess border-destructive inset-edge border-l-2 px-3.5 py-2.5 font-mono text-[12.5px] leading-[1.55]"
				data-in={i < shown ? '' : undefined}
				aria-hidden={i < shown ? undefined : 'true'}
			>
				<span class="text-dim">Ln {err.line}</span>
				<span class="text-foreground ml-2">{err.text}</span>
				<span class="text-dim ml-2 tracking-[.04em]">{err.code}</span>
			</div>
		{/each}
	</div>
</div>

<style>
	/* Squiggles are hidden until the widget lands, then draw in one after another. */
	.diagnostics-widget :global(.shiki .shaku-underline) {
		opacity: 0;
		transition: opacity 180ms ease-out;
	}
	.diagnostics-widget[data-in] :global(.shiki .shaku-underline:nth-of-type(1)) {
		opacity: 1;
		transition-delay: 300ms;
	}
	.diagnostics-widget[data-in] :global(.shiki .shaku-underline:nth-of-type(2)) {
		opacity: 1;
		transition-delay: 720ms;
	}
	.diagnostics-widget[data-in] :global(.shiki .shaku-underline:nth-of-type(3)) {
		opacity: 1;
		transition-delay: 1140ms;
	}
	@media (prefers-reduced-motion: reduce) {
		.diagnostics-widget :global(.shiki .shaku-underline) {
			opacity: 1;
		}
	}
</style>
