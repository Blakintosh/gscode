import { githubUrl } from '$lib/data/site';
import type { LayoutServerLoad } from './$types';

/**
 * The header shows the repository's star count next to the GitHub icon. Fetched server-side and
 * held for an hour, so visitors never wait on the GitHub API and the site stays well inside the
 * unauthenticated rate limit. `stars` is null until the first successful fetch; a failure keeps
 * the last good value and still refreshes the timestamp, so an outage cannot turn into a
 * per-request hammering of the API.
 */
const TTL_MS = 60 * 60 * 1000;

let cache: { stars: number | null; fetchedAt: number } = { stars: null, fetchedAt: 0 };

export const load = (async ({ fetch }) => {
	if (Date.now() - cache.fetchedAt > TTL_MS) {
		let stars = cache.stars;
		try {
			const repo = new URL(githubUrl).pathname.replace(/^\/|\/$/g, '');
			const response = await fetch(`https://api.github.com/repos/${repo}`, {
				headers: { Accept: 'application/vnd.github+json' }
			});
			if (response.ok) {
				const body = (await response.json()) as { stargazers_count?: number };
				stars = body.stargazers_count ?? stars;
			}
		} catch {
			/* keep the last good value */
		}
		cache = { stars, fetchedAt: Date.now() };
	}

	return { githubStars: cache.stars };
}) satisfies LayoutServerLoad;
