<script module lang="ts">
	import type { Tabs as TabsPrimitive } from 'bits-ui';

	export type CodeTabProps = TabsPrimitive.TriggerProps;
</script>

<script lang="ts">
	import * as Tabs from '$lib/components/ui/tabs/index.js';
	import { cn } from '$lib/utils';

	let { class: className, children, ...restProps }: CodeTabProps = $props();
</script>

<Tabs.Trigger class={cn('normal-case tracking-[.04em]', className)} {...restProps}>
	{@render children?.()}
</Tabs.Trigger>
