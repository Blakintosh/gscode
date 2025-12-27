import { Editor } from '$lib/api-editor/editor.svelte';
import type { LayoutLoad } from './$types';

export const load = (async () => {
    // Create an empty editor - user will load a library via the UI
    const editor = Editor.empty();

    return {
        editor
    };
}) satisfies LayoutLoad;
