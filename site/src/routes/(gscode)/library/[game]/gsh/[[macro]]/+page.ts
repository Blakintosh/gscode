import { error } from '@sveltejs/kit';
import type { PageLoad } from './$types';

export const load = (async ({ params, parent }) => {
	const { macroMap, game } = await parent();
	const first = macroMap.keys().next();
	const macroName = params.macro ?? (first.done ? '' : first.value);

	if (!macroName || !macroMap.has(macroName.toLowerCase())) {
		error(404, "That macro doesn't exist.");
	}

	return {
		game,
		macro: macroMap.get(macroName.toLowerCase())
	};
}) satisfies PageLoad;
