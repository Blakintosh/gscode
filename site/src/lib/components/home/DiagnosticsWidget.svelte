<script lang="ts">
	/**
	 * The diagnostics story: a GSC file whose three problems are underlined one after
	 * another as the widget scrolls into view, each problem row landing just after its
	 * squiggle. The underlines are shaku annotations in the code block; their staggered
	 * reveal is CSS keyed off `data-in` on this element.
	 */
	import * as Code from '$lib/components/ui/code';
	import { reducedMotion } from '$lib/actions/reveal';

	let { active = false }: { active?: boolean } = $props();

	const source = `#insert scripts\\shared\\shrd.gsh;
//      ~~~~~~~~~~~~~~~~~~~~~~~
function write_some_code( weapon_name )
{
 w_weapon = GetWeapon( weapon_name );
 ammo = 30;
 foreach ( bullet in ammo )
//                   ~~~~
 {
  self GiveMaxAmmo( w_weapon );
 }
 return ammo
//         ~
}`;

	// Codes and wording are the server's own — see DiagnosticMessages.cs; the editor
	// shows them as `gscode-NNNN`, and 5033 is a warning there, not an error.
	const errors = [
		{
			line: 1,
			code: 'gscode-2006',
			severity: 'error',
			text: "Cannot find insert file 'scripts\\shared\\shrd.gsh'."
		},
		{
			line: 6,
			code: 'gscode-5033',
			severity: 'warning',
			text: "'foreach' needs an array or a struct, but this is int."
		},
		{ line: 10, code: 'gscode-3014', severity: 'error', text: "Expected ';' at the end of this statement." }
	];

	// Number of problem rows shown so far; steps up in time with the underlines.
	let shown = $state(0);
	const shownErrors = $derived(errors.slice(0, shown).filter((e) => e.severity === 'error').length);
	const shownWarnings = $derived(shown - shownErrors);
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

	<!-- The Problems panel: mono rows on the recess, a 2px severity rule — Clip red for errors, amber for the warning. -->
	<div class="flex flex-col gap-2 self-start">
		<p class="type-label text-dim flex items-center justify-between">
			<span>Problems</span>
			<span class="tabular-nums"
				><span class="text-destructive">{shownErrors} err</span>
				<span aria-hidden="true">·</span>
				<span class="text-warning">{shownWarnings} warn</span></span
			>
		</p>
		{#each errors as err, i (err.code)}
			<div
				class="reveal bg-recess inset-edge border-l-2 px-3.5 py-2.5 font-mono text-sm leading-normal {err.severity ===
				'warning'
					? 'border-warning'
					: 'border-destructive'}"
				data-in={i < shown ? '' : undefined}
				aria-hidden={i < shown ? undefined : 'true'}
			>
				<span class="text-dim">Ln {err.line}</span>
				<span class="text-foreground ml-2">{err.text}</span>
				<span class="text-dim ml-2 tracking-data">{err.code}</span>
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
