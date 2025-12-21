import { ApiLibrarian } from '$lib/app/library/api.svelte';
import { Editor } from '$lib/api-editor/editor.svelte';
import { error } from '@sveltejs/kit';
import type { LayoutLoad } from './$types';

export const load = (async ({ fetch, params }) => {
    const languageId = params.languageId.toLowerCase();

    if (languageId !== 'gsc' && languageId !== 'csc') {
        throw error(404, 'Invalid language. Only GSC and CSC are supported.');
    }

    const gameId = "t7";

    const librarian = await ApiLibrarian.initialise({ gameId, languageId, fetch });
    const library = await librarian.library;
    const editor = Editor.fromLibrary(library);

    return {
        librarian,
        editor,
        languageId
    };
}) satisfies LayoutLoad;
