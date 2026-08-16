<script module lang="ts">
	import type { Tabs as TabsPrimitive } from 'bits-ui';

	export type CodeTabsProps = TabsPrimitive.ListProps;
</script>

<script lang="ts">
	import * as Tabs from '$lib/components/ui/tabs/index.js';
	import { cn } from '$lib/utils';

	let { class: className, children, ...restProps }: CodeTabsProps = $props();
</script>

<!-- File-tab rail on the raise colour; sits inside the rim so the tab dock stays clear. -->
<Tabs.List
	class={cn('bg-popover w-full justify-start gap-0 pl-[36px] text-[10px]', className)}
	{...restProps}
>
	{@render children?.()}
</Tabs.List>
