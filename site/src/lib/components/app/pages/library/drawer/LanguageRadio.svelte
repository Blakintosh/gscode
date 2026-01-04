<script lang="ts">
	import * as HoverCard from '$components/ui/hover-card';
    import * as Tabs from '$lib/components/ui/tabs';
	import LanguageInfo from './LanguageInfo.svelte';
	import { page } from '$app/stores';

    type Props = {
        onLanguageChange: (value: string | undefined) => void;
    }

    let {
        onLanguageChange
    }: Props = $props();
</script>

<Tabs.Root value={$page.data.languageId} class="w-full" onValueChange={onLanguageChange}>
	<Tabs.List class="w-full grid grid-cols-2">
		<Tabs.Trigger value="gsc">
            GSC
        </Tabs.Trigger>
		<Tabs.Trigger value="csc">
            CSC
        </Tabs.Trigger>
	</Tabs.List>
</Tabs.Root>
