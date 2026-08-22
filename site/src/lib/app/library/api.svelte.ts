import { ScrLibrarySchema, type ScrLibrary } from '$lib/models/library';

type LoadApiParams = {
	fetch: typeof globalThis.fetch;
	gameId: string;
	languageId: string;
};

function libraryUrl(gameId: string, languageId: string) {
	return `/api/getLibrary?gameId=${encodeURIComponent(gameId)}&languageId=${encodeURIComponent(languageId)}`;
}

/**
 * Holds one game/language library, refetching when either changes.
 *
 * The cache is keyed on what was REQUESTED, not on what came back. Those differ: the URL and the
 * `gscode.game` setting spell Black Ops III `bo3`, while its artifacts — and therefore the payload's
 * own `gameId` — say `t7`. Comparing against the payload made every hit a miss and refetched 2.9 MB
 * on each read.
 */
export class ApiLibrarian {
	gameId: string = $state('');
	languageId: string = $state('');

	library: Promise<ScrLibrary> = $derived(this.loadLibrary(this.gameId, this.languageId));

	private _cachedKey: string;
	private _cachedLibrary: ScrLibrary;

	private constructor(gameId: string, languageId: string, initialData: ScrLibrary) {
		this.gameId = gameId;
		this.languageId = languageId;
		this._cachedKey = ApiLibrarian.key(gameId, languageId);
		this._cachedLibrary = initialData;
	}

	/**
	 * Creates an API librarian, loading the first library through the caller's fetch — the server's
	 * during SSR, so the first paint does not wait on a round trip from the browser.
	 */
	static async initialise({ fetch, gameId, languageId }: LoadApiParams) {
		const library = await ApiLibrarian.getLibrary({ fetch, gameId, languageId });
		return new ApiLibrarian(gameId, languageId, library);
	}

	private static key(gameId: string, languageId: string) {
		return `${gameId}/${languageId}`;
	}

	private async loadLibrary(gameId: string, languageId: string): Promise<ScrLibrary> {
		const key = ApiLibrarian.key(gameId, languageId);
		if (this._cachedKey === key) {
			return this._cachedLibrary;
		}

		const library = await ApiLibrarian.getLibrary({ fetch, gameId, languageId });
		this._cachedKey = key;
		this._cachedLibrary = library;
		return library;
	}

	private static async getLibrary({ fetch, gameId, languageId }: LoadApiParams) {
		const res = await fetch(libraryUrl(gameId, languageId));
		if (!res.ok) {
			throw new Error(`Could not load the ${gameId} ${languageId.toUpperCase()} library.`);
		}
		return ScrLibrarySchema.parseAsync(await res.json());
	}
}
