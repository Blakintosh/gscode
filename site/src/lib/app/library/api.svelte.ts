import { ScrLibrarySchema, type ScrLibrary } from "$lib/models/library";
import { onMount } from "svelte";
import { z } from "zod";
import { getLibrary } from "../../../routes/(gscode)/library/library";

type LoadApiParams = {
    fetch: any;
    gameId: string;
    languageId: string;
}

export async function useApi(params: LoadApiParams) {
    let gameId = $state(params.gameId);
    let languageId = $state(params.languageId);
    let fetcher = $state(params.fetch);

    let _library: ScrLibrary | null = null;
    async function loadLibrary({ fetch, gameId, languageId }: LoadApiParams) {
        // Already loaded
        if(_library?.gameId == gameId && _library?.languageId == languageId) {
            return _library;
        }

        const res = await fetch(`/api/getLibrary?gameId=${gameId}&languageId=${languageId}`);

        _library = await res.json() as ScrLibrary;
        return _library;
    }

    let library: Promise<ScrLibrary> = $derived(loadLibrary({ fetch: fetcher, gameId, languageId}));
    return {
        get library() { return library },
        set gameId(newValue: string) {
            fetcher = fetch;
            gameId = newValue
        },
        set languageId(newValue: string) {
            fetcher = fetch;
            languageId = newValue
        }
    }
}

export class ApiLibrarian {
    gameId: string = $state("");
    languageId: string = $state("");

    library: Promise<ScrLibrary> = $derived(this.loadLibrary());

    private _cachedLibrary: ScrLibrary;

    /**
     * Instantiates the API librarian, loading the API server-side.
     * @param gameId The initial game ID
     * @param languageId The initial language ID
     * @param fetcher The server-side fetch function
     */
    private constructor(gameId: string, languageId: string, initialData: ScrLibrary) {
        this.gameId = gameId;
        this.languageId = languageId;
        this._cachedLibrary = initialData;
    }

    /**
     * Creates an API librarian.
     * @param param0 Game ID, language ID, and desired fetch function (allows for server-side fetching)
     * @returns The initialised API librarian
     */
    static async initialise({ fetch, gameId, languageId }: LoadApiParams) {
        const library = await ApiLibrarian.getLibrary({ fetch, gameId, languageId });
        return new ApiLibrarian(gameId, languageId, library);
    }

    private async loadLibrary(): Promise<ScrLibrary> {
        // Already loaded, return the cached library
        if(this._cachedLibrary?.gameId == this.gameId && this._cachedLibrary?.languageId == this.languageId) {
            return this._cachedLibrary;
        }

        this._cachedLibrary = await ApiLibrarian.getLibrary({ fetch: fetch, gameId: this.gameId, languageId: this.languageId });
        return this._cachedLibrary;
    }

    private static async getLibrary({ fetch, gameId, languageId }: LoadApiParams) {
        const res = await fetch(`/api/getLibrary?gameId=${gameId}&languageId=${languageId}`);
        return ScrLibrarySchema.parseAsync(await res.json());
    }
}