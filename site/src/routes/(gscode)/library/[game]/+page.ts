import { error, redirect } from '@sveltejs/kit';
import { defaultGameSlug, findGame, isLanguageId, languagesFor } from '$lib/data/games';
import type { PageLoad } from './$types';

/**
 * A single segment under `/library` — either a game that wants its default language, or one of the
 * pre-multi-game URLs.
 *
 * `/library/gsc` and `/library/csc` were the shape the extension shipped and the wiki links to, so
 * they are answered permanently rather than 404'd. Everything the extension has ever opened keeps
 * working, which is what lets the URL contract change without a flag day.
 */
export const load: PageLoad = async ({ params }) => {
	const segment = params.game.toLowerCase();

	if (isLanguageId(segment)) {
		redirect(301, `/library/${defaultGameSlug}/${segment}`);
	}

	const game = findGame(segment);
	if (!game) {
		error(404, `There is no library for "${params.game}".`);
	}

	// `t7` resolves, but the canonical URL spells the game the way the extension setting does.
	redirect(game.slug === segment ? 302 : 301, `/library/${game.slug}/${languagesFor(game)[0]}`);
};
