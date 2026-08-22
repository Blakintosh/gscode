import { error, redirect } from '@sveltejs/kit';
import { ApiLibrarian } from '$lib/app/library/api.svelte';
import { defaultGameSlug, findGame, isLanguageId, languagesFor } from '$lib/data/games';
import type { ScrFunction, ScrLibrary } from '$lib/models/library';
import type { LayoutLoad } from './$types';

export const load = (async ({ fetch, params, url }) => {
	const gameSegment = params.game.toLowerCase();
	const languageSegment = params.languageId.toLowerCase();

	// The pre-multi-game function URL: `/library/gsc/<name>`. Two segments, so it lands here with
	// the language sitting in the game's place.
	if (isLanguageId(gameSegment)) {
		redirect(301, `/library/${defaultGameSlug}/${gameSegment}/${languageSegment}`);
	}

	const game = findGame(gameSegment);
	if (!game) {
		error(404, `There is no library for "${params.game}".`);
	}

	if (game.slug !== gameSegment) {
		// Reached by data prefix (`t7`); settle on the canonical spelling.
		redirect(301, `/library/${game.slug}/${languageSegment}`);
	}

	if (!isLanguageId(languageSegment)) {
		error(404, `There is no ${languageSegment.toUpperCase()} library.`);
	}

	const languages = languagesFor(game);
	if (!languages.includes(languageSegment)) {
		// A real language, but not one this game has — Call of Duty 4 and Modern Warfare 2 ship no
		// client scripts, so CSC does not exist there. The game still has exactly one library and
		// that is where the answer is, so this is an over-specified URL rather than a wrong one.
		// Reached by the extension whenever a .csc buffer is open under one of those two games.
		// The function segment belongs to the child route, so it is not in this layout's params;
		// take it from the path so `/library/cod4/csc/abs` keeps the `abs`.
		const rest = url.pathname.split('/').slice(4).join('/');
		redirect(301, `/library/${game.slug}/${languages[0]}${rest ? `/${rest}` : ''}`);
	}

	const librarian = await ApiLibrarian.initialise({
		gameId: game.slug,
		languageId: languageSegment,
		fetch
	});

	return {
		librarian,
		libraryMap: convertLibraryToMap(await librarian.library),
		provenance: (await librarian.library).provenance ?? null,
		game,
		languageId: languageSegment
	};
}) satisfies LayoutLoad;

function convertLibraryToMap(library: ScrLibrary) {
	const apiFunctions: Map<string, ScrFunction> = new Map();

	for (const entry of library.api) {
		apiFunctions.set(entry.name.toLowerCase(), entry);
	}
	return apiFunctions;
}
