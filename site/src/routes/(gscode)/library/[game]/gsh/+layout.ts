import { error, redirect } from '@sveltejs/kit';
import { findGame, languagesFor } from '$lib/data/games';
import { GshLibrarySchema, type GshMacro } from '$lib/models/macros';
import type { LayoutLoad } from './$types';

/**
 * The macro reference: `/library/bo3/gsh`. A literal segment, so it wins over `[languageId]` and
 * the function-library machinery never has to know macros exist.
 */
export const load = (async ({ fetch, params }) => {
	const gameSegment = params.game.toLowerCase();

	const game = findGame(gameSegment);
	if (!game) {
		error(404, `There is no library for "${params.game}".`);
	}

	if (game.slug !== gameSegment) {
		// Reached by data prefix (`t7`); settle on the canonical spelling.
		redirect(301, `/library/${game.slug}/gsh`);
	}

	if (!game.hasMacros) {
		// Over-specified rather than wrong, like `/library/cod4/csc`: the game has a library,
		// just no documented macros. Land on the library it does have.
		redirect(301, `/library/${game.slug}/${languagesFor(game)[0]}`);
	}

	const response = await fetch(`/api/getMacros?gameId=${game.slug}`);
	if (!response.ok) {
		error(404, `No macro library is published for ${game.name}.`);
	}
	const library = GshLibrarySchema.parse(await response.json());

	const macroMap = new Map<string, GshMacro>();
	for (const macro of library.macros) {
		macroMap.set(macro.name.toLowerCase(), macro);
	}

	// Every header that defines at least one macro, for the file filter. Sorted, so mp/, shared/
	// and zm/ fall into natural groups.
	const files = [
		...new Set(library.macros.flatMap((macro) => macro.definitions.map((d) => d.path)))
	].sort();

	return {
		game,
		languageId: 'gsh',
		library,
		macroMap,
		files
	};
}) satisfies LayoutLoad;
