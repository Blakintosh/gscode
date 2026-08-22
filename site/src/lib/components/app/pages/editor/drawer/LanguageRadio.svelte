<script lang="ts">
	import * as Tabs from '$lib/components/ui/tabs';
	import { page } from '$app/stores';

	type Props = {
		onLanguageChange: (value: string | undefined) => void;
	};

	let { onLanguageChange }: Props = $props();
</script>

<Tabs.Root value={$page.data.languageId} class="w-full" onValueChange={onLanguageChange}>
	<Tabs.List class="grid w-full grid-cols-2">
		<Tabs.Trigger value="gsc">GSC</Tabs.Trigger>
		<Tabs.Trigger value="csc">CSC</Tabs.Trigger>
	</Tabs.List>
</Tabs.Root>
