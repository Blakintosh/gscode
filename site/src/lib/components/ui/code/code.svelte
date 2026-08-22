<script module lang="ts">
	import type { Tabs as TabsPrimitive } from 'bits-ui';

	export type CodeProps = TabsPrimitive.RootProps & {
		/** Language readout in the bottom-right corner (GSC · CSC · GSH). */
		language?: string;
		/** Small mono tag docked in the top-left cut, e.g. "example" or "before". */
		tab?: string;
		/** The surface behind the panel (what handles and readout paint over). */
		behind?: 'card' | 'popover' | 'background' | 'recess' | 'table';
	};
</script>

<script lang="ts">
	/**
	 * A code panel is a Datum brush: rim + handles, tab top-left, language readout
	 * bottom-right, code on the table surface one step below the page.
	 */
	import { cn } from '$lib/utils';
	import * as Tabs from '$lib/components/ui/tabs/index.js';
	import Brush from '$lib/components/site/Brush.svelte';

	let {
		class: className,
		language = 'GSC',
		tab,
		behind = 'background',
		children,
		...restProps
	}: CodeProps = $props();
</script>

<Brush
	surface="table"
	{behind}
	handles
	{tab}
	readout={language}
	class={cn('my-3', className)}
	bodyClass="chamfer m-px flex flex-col"
>
	<Tabs.Root class="flex flex-col gap-0" {...restProps}>
		{@render children?.()}
	</Tabs.Root>
</Brush>
