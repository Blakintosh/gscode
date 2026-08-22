import type { Action } from 'svelte/action';

/**
 * Marks an element `data-in` once it scrolls into view (and never unmarks it), and calls
 * `onIn` at that moment so a widget can start its sequence. Pair with the `reveal` utility
 * for the fade/translate, or read `data-in` yourself. Fires immediately when the user
 * prefers reduced motion so nothing waits on an animation that will not play.
 */
export const reveal: Action<HTMLElement, { onIn?: () => void; threshold?: number } | undefined> = (
	node,
	options
) => {
	let fired = false;
	const fire = () => {
		if (fired) return;
		fired = true;
		node.dataset.in = '';
		options?.onIn?.();
	};

	if (typeof IntersectionObserver === 'undefined') {
		fire();
		return;
	}
	const observer = new IntersectionObserver(
		(entries) => {
			if (entries.some((e) => e.isIntersecting)) {
				fire();
				observer.disconnect();
			}
		},
		{ threshold: options?.threshold ?? 0.35, rootMargin: '0px 0px -10% 0px' }
	);
	observer.observe(node);
	return { destroy: () => observer.disconnect() };
};

/** True when the visitor prefers reduced motion — widgets jump to their final state. */
export function reducedMotion(): boolean {
	return typeof matchMedia !== 'undefined' && matchMedia('(prefers-reduced-motion: reduce)').matches;
}
