<script module lang="ts">
	import type { Tabs as TabsPrimitive } from 'bits-ui';

	export type CodeExampleProps = TabsPrimitive.ContentProps;
</script>

<script lang="ts">
	import * as Tabs from '$lib/components/ui/tabs/index.js';
	import { cn } from '$lib/utils';

	let { class: className, children, ...restProps }: CodeExampleProps = $props();
</script>

<Tabs.Content class={cn('m-0 overflow-x-auto', className)} {...restProps}>
	{@render children?.()}
</Tabs.Content>
