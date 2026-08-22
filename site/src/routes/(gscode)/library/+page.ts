import { redirect } from '@sveltejs/kit';
import { defaultGameSlug } from '$lib/data/games';
import type { PageLoad } from './$types';

export const load: PageLoad = async () => {
	redirect(302, `/library/${defaultGameSlug}/gsc`);
};
