import { error } from '@sveltejs/kit';
import type { PageLoad } from './$types';

export const load = (async ({ params, parent }) => {
    const { editor, languageId } = await parent();
    const functionName = params.func ?? [...editor.functions.keys()][0];

    const functionEditor = editor.getFunction(functionName);
    if (!functionEditor) {
        throw error(404, "That function doesn't exist.");
    }

    return {
        languageId,
        functionEditor
    };
}) satisfies PageLoad;
