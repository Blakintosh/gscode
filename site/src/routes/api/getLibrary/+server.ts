import { error, json } from '@sveltejs/kit';
import type { RequestHandler } from './$types';

import * as gscApiT7 from "$lib/apiSource/t7_api_gsc.json";
import * as cscApiT7 from "$lib/apiSource/t7_api_csc.json";

/**
 * API route that serves the script library requests made by the GSCode Language Server
 * for more up-to-date API references compared to what gets locally stored in each version
 */
export const GET = (({ url }) => {
	const gameId = url.searchParams.get("gameId");
	const languageId = url.searchParams.get("languageId");

	if(gameId === "t7") {
		if(languageId === "gsc") {
			return json(gscApiT7);
		} else if(languageId === "csc") {
			return json(cscApiT7);
		}
	}

	// Not found
	error(400, "Not a valid recognised game ID or language ID.");
}) satisfies RequestHandler;