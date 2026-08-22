<script lang="ts">
	/**
	 * The gscode mark — a datum plane seen edge-on: a 26-unit chamfered square (8-unit cuts,
	 * top-left and bottom-right) with the reference line cut out at 44–56% of the height.
	 * Rim gradient bright → teal → violet. Never rotate it, never round it, never fill it
	 * a solid warm colour. Minimum 16px on screen.
	 */
	import { cn } from '$lib/utils.js';

	let { class: className = '' }: { class?: string } = $props();
	// Unique per instance (SSR-stable) so several marks on a page don't share a gradient id.
	const uid = $props.id();
	const id = 'datum-rim-' + uid;
</script>

<svg
	xmlns="http://www.w3.org/2000/svg"
	viewBox="0 0 26 26"
	aria-hidden="true"
	focusable="false"
	class={cn('block shrink-0', className)}
>
	<defs>
		<linearGradient {id} x1="0" y1="0" x2="0.62" y2="0.79">
			<stop offset="0" stop-color="#7BEFDD" />
			<stop offset="0.58" stop-color="#3ED1BD" />
			<stop offset="1" stop-color="#8B7BFF" />
		</linearGradient>
	</defs>
	<path fill="url(#{id})" d="M8 0 H26 V11.44 H0 V8 Z M0 14.56 H26 V18 L18 26 H0 Z" />
</svg>
