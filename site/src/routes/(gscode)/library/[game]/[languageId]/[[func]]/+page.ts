import { error } from '@sveltejs/kit';
import type { PageLoad } from './$types';

export const load = (async ({ params, parent }) => {
	const { libraryMap, languageId, game } = await parent();
	const first = libraryMap.keys().next();
	const functionName = params.func ?? (first.done ? '' : first.value);

	if (!functionName || !libraryMap.has(functionName.toLowerCase())) {
		error(404, "That function doesn't exist.");
	}

	return {
		game,
		languageId,
		func: libraryMap.get(functionName.toLowerCase())
	};
}) satisfies PageLoad;
