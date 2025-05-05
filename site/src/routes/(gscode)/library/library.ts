import type { ScrFunction, ScrLibrary } from "$lib/models/library";

export async function getLibrary(fetch: any, gameId: string, languageId: string) {
	const res = await fetch(`/api/getLibrary?gameId=${gameId}&languageId=${languageId}`);
	const library: ScrLibrary = await res.json();

    return library;
}