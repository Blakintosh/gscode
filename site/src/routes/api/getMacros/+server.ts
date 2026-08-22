import { error, json } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import { findGame } from '$lib/data/games';

/**
 * Serves a game's macro library — the stock `.gsh` macros documented in `data/macros/`, carried
 * into `apiSource/` by `npm run sync:api` like the function artifacts.
 *
 * Lazy for the same reason as `/api/getLibrary`: the artifact runs to ~2 MB and only one game has
 * one, so it loads on the first request that wants it rather than riding in the server bundle.
 */
const sources = import.meta.glob<{ default: unknown }>('$lib/apiSource/*_macros_*.json');

const cache = new Map<string, Promise<unknown>>();

function load(prefix: string): Promise<unknown> {
	const existing = cache.get(prefix);
	if (existing) {
		return existing;
	}

	const path = `/src/lib/apiSource/${prefix}_macros_gsh.json`;
	const importer = sources[path];
	if (!importer) {
		return Promise.reject(new Error(`No macro artifact at ${path}.`));
	}

	const pending = importer()
		.then((module) => module.default ?? module)
		.catch((cause) => {
			// A failed load must not be remembered, or one bad request poisons the key for the
			// life of the process.
			cache.delete(prefix);
			throw cause;
		});

	cache.set(prefix, pending);
	return pending;
}

export const GET = (async ({ url, setHeaders }) => {
	const gameId = url.searchParams.get('gameId');

	const game = findGame(gameId ?? undefined);
	if (!game || !game.hasMacros) {
		error(400, `No macro library exists for "${gameId ?? ''}".`);
	}

	let library: { revision?: number };
	try {
		library = (await load(game.prefix)) as { revision?: number };
	} catch {
		error(404, `No macro library is published for ${game.name}.`);
	}

	setHeaders({
		ETag: `"${game.prefix}-gsh-${library.revision ?? 0}"`,
		'Cache-Control': 'public, max-age=0, must-revalidate'
	});

	return json(library);
}) satisfies RequestHandler;
