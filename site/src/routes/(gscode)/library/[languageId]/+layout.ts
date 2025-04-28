import { ApiLibrarian, useApi } from '$lib/app/library/api.svelte';
import type { ScrFunction, ScrLibrary } from '$lib/models/library';
import { getLibrary } from '../library';
import type { LayoutLoad } from './$types';

export const load = (async ({ fetch, params }) => {
    const languageId = params.languageId.toLowerCase() as string;
	const gameId = "t7";

    const librarian = await ApiLibrarian.initialise({ gameId, languageId, fetch });

    return {
        librarian,
        libraryMap: convertLibraryToMap(await librarian.library),
        languageId
    };
}) satisfies LayoutLoad;

function convertLibraryToMap(library: ScrLibrary) {
    const apiFunctions: Map<string, ScrFunction> = new Map();
    
    for(const entry of library.api) {
        apiFunctions.set(entry.name.toLowerCase(), entry);
    }
    return apiFunctions;
}