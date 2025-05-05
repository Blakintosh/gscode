import { error } from '@sveltejs/kit';
import type { PageLoad } from './$types';

export const load = (async ({ params, parent }) => {
    const { librarian, libraryMap, languageId } = await parent();
    const functionName = params.func ?? [...libraryMap][0][0];
    
    if(!libraryMap.has(functionName.toLowerCase())) {
        throw error(404, "That function doesn't exist.");
    }

    return {
        languageId,
        func: libraryMap.get(functionName.toLowerCase())
    };
}) satisfies PageLoad;