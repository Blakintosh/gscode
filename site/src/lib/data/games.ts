/**
 * The games the site serves a builtin library for.
 *
 * This mirrors `server/src/GSCode.Core/Profiles/SupportedProfiles.cs` — one entry per profile that
 * sets a `DataFilePrefix`. Keep the two in step: a game gains a page here only once the language
 * server actually ships its artifacts, since the payload is those artifacts.
 *
 * Two identifiers, deliberately:
 *
 * - `slug` is what the URL and `gscode.game` spell (`bo3`), matching the profile's ShortName. It is
 *   what a person would type and what the extension already has in hand.
 * - `prefix` is what the data files are NAMED (`t7`), matching the profile's DataFilePrefix, and is
 *   what the payload's own `gameId` says.
 *
 * They differ for exactly one game, and collapsing them would either put `t7` in a user-facing URL
 * or rename five committed artifacts.
 */

/** What kind of source a game's library was built from — drives how much the page may claim. */
export type LibrarySource =
	/** Per-function documentation pages: signatures are stated, not inferred. */
	| 'documentation'
	/** A mod-tools syntax wordfile: a name list, with signatures inherited from a sibling game. */
	| 'wordfile'
	/** No source of its own; names taken from a sibling and corrected by sweeping shipped scripts. */
	| 'reconstructed';

export type GameEntry = {
	/** URL segment and `gscode.game` value. */
	slug: string;
	/** Data-file prefix — the payload's `gameId`. */
	prefix: string;
	name: string;
	shortName: string;
	year: number;
	/** Whether this game has client scripts, and therefore a CSC library. */
	hasClientScripts: boolean;
	/**
	 * How the dialect imports — the deepest split in the lineage, and the server's `ImportStyle`.
	 * `include` merges a file's functions into the caller's scope and reaches them by path;
	 * `using` imports a namespace and keeps calls qualified. Black Ops III is the only game on the
	 * second, and almost every dialect difference follows from which side a game is on.
	 */
	imports: 'include' | 'using';
	source: LibrarySource;
	/**
	 * Whether the function list is exhaustive enough to say a name is NOT an engine function.
	 * `HasCompleteBuiltinLibrary` on the profile.
	 */
	complete: boolean;
	/**
	 * Whether the PARAMETERS on an entry can be judged against — a separate, narrower claim than
	 * completeness. `HasReliableBuiltinSignatures` on the profile.
	 */
	reliableSignatures: boolean;
	/** The game whose entries fill in this one's, where they are borrowed rather than its own. */
	inheritsFrom?: string;
};

export const games: GameEntry[] = [
	{
		slug: 'cod4',
		prefix: 'cod4',
		name: 'Call of Duty 4: Modern Warfare',
		shortName: 'CoD4',
		year: 2007,
		hasClientScripts: false,
		imports: 'include',
		source: 'documentation',
		complete: true,
		reliableSignatures: true
	},
	{
		slug: 'waw',
		prefix: 'waw',
		name: 'Call of Duty: World at War',
		shortName: 'WaW',
		year: 2008,
		hasClientScripts: true,
		imports: 'include',
		source: 'wordfile',
		complete: false,
		reliableSignatures: false,
		inheritsFrom: 'cod4'
	},
	{
		slug: 'mw2',
		prefix: 'mw2',
		name: 'Call of Duty: Modern Warfare 2',
		shortName: 'MW2',
		year: 2009,
		hasClientScripts: false,
		imports: 'include',
		source: 'reconstructed',
		complete: false,
		reliableSignatures: false,
		inheritsFrom: 'cod4'
	},
	{
		slug: 'bo1',
		prefix: 'bo1',
		name: 'Call of Duty: Black Ops',
		shortName: 'BO1',
		year: 2010,
		hasClientScripts: true,
		imports: 'include',
		source: 'wordfile',
		complete: false,
		reliableSignatures: false,
		inheritsFrom: 'cod4'
	},
	{
		slug: 'bo3',
		prefix: 't7',
		name: 'Call of Duty: Black Ops III',
		shortName: 'BO3',
		year: 2015,
		hasClientScripts: true,
		imports: 'using',
		source: 'documentation',
		complete: true,
		reliableSignatures: true
	}
];

/** The game a bare `/library` link means, and the extension's default. */
export const defaultGameSlug = 'bo3';

export const languageIds = ['gsc', 'csc'] as const;
export type LanguageId = (typeof languageIds)[number];

export function isLanguageId(value: string): value is LanguageId {
	return (languageIds as readonly string[]).includes(value);
}

/** Looks a game up by its URL slug, or by its data prefix as a fallback (so `t7` resolves too). */
export function findGame(slug: string | undefined): GameEntry | undefined {
	if (!slug) {
		return undefined;
	}

	const wanted = slug.toLowerCase();
	return games.find((game) => game.slug === wanted) ?? games.find((game) => game.prefix === wanted);
}

/** The languages a game actually has. CSC exists only where the game ships client scripts. */
export function languagesFor(game: GameEntry): LanguageId[] {
	return game.hasClientScripts ? ['gsc', 'csc'] : ['gsc'];
}

export function defaultGame(): GameEntry {
	return findGame(defaultGameSlug) ?? games[games.length - 1];
}
