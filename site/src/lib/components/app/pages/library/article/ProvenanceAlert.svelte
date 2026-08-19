<script lang="ts">
	import BookOpenText from '@lucide/svelte/icons/book-open-text';
	import FileSearch from '@lucide/svelte/icons/file-search';
	import Wand from '@lucide/svelte/icons/wand';
	import FlagAlert from './FlagAlert.svelte';
	import type { ScrProvenance } from '$lib/models/library';
	import { findGame, type GameEntry } from '$lib/data/games';

	type Props = {
		provenance: ScrProvenance | null;
		game: GameEntry;
		class?: string;
	};

	let { provenance, game, class: className = '' }: Props = $props();

	const inherited = $derived(
		provenance?.inheritsFrom ? (findGame(provenance.inheritsFrom)?.name ?? null) : null
	);

	/**
	 * What this library is, said once at the top of the page rather than per entry.
	 *
	 * The distinction the language server draws is worth keeping here: a complete list of NAMES is a
	 * different claim from reliable SIGNATURES, and a game can have the first without the second.
	 * Only Call of Duty 4 and Black Ops III have both, so for the other three this page is showing a
	 * plausible signature for a related function rather than a verified one for this game — and
	 * saying so is the whole point of carrying provenance across.
	 */
	const notice = $derived.by(() => {
		if (!provenance || (provenance.complete && provenance.reliableSignatures)) {
			return null;
		}

		if (provenance.source === 'reconstructed') {
			return {
				Icon: Wand,
				title: 'Reconstructed library',
				description:
					`${game.name} shipped no mod tools, so this library has no documentation behind it. ` +
					`Its names come from ${inherited ?? 'a sibling game'} and from sweeping the game's own ` +
					`shipped scripts; entries marked reconstructed had their parameters inferred from call ` +
					`sites. Treat signatures as a starting point, not a specification.`
			};
		}

		return {
			Icon: FileSearch,
			title: 'Partial library',
			description:
				`${game.name}'s function list comes from its mod-tools wordfile, which carries names but ` +
				`no signatures` +
				(inherited
					? `, so the parameters shown are inherited from ${inherited} where the two share a ` +
						`function. They are a plausible signature for a related function, not a verified one ` +
						`for this game.`
					: `. Parameters shown are unverified for this game.`) +
				` The list is also known to be incomplete, so a name missing here may still exist.`
		};
	});

	const documented = $derived(
		provenance?.complete === true &&
			provenance?.reliableSignatures === true &&
			provenance?.source === 'documentation'
	);
</script>

{#if notice}
	<div class={className}>
		<FlagAlert Icon={notice.Icon} title={notice.title} description={notice.description} />
	</div>
{:else if documented}
	<div class={className}>
		<FlagAlert
			Icon={BookOpenText}
			title="Documented library"
			description={`Built from ${game.name}'s own per-function documentation. Signatures here are stated by the source rather than inferred, and the function list is complete enough to say a name is not an engine function.`}
		/>
	</div>
{/if}
