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
            <HoverCard.Root>
                <HoverCard.Trigger>GSC</HoverCard.Trigger>
                <LanguageInfo name={"GSC"} version={3} description={"The language used for server-side scripting logic. Offers a richer API than CSC."} revisedOn={"Oct 1 2021"}/>
            </HoverCard.Root>
        </Tabs.Trigger>
		<Tabs.Trigger value="csc">
            <HoverCard.Root>
                <HoverCard.Trigger>CSC</HoverCard.Trigger>
                <LanguageInfo name={"CSC"} version={1} description={"The language used for client-side scripting logic. It is typically more responsive."} revisedOn={"Oct 1 2021"}/>
            </HoverCard.Root>
        </Tabs.Trigger>
	</Tabs.List>
</Tabs.Root>
