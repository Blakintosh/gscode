<script lang="ts">
	/**
	 * Accessibility font switch. Sora is the brand face; OpenDyslexic is offered as an
	 * alternative and applied on <html> so it overrides display and body type alike.
	 */
	import TypeIcon from '@lucide/svelte/icons/type';
	import { Button } from '$lib/components/ui/button';
	import * as DropdownMenu from '$lib/components/ui/dropdown-menu';

	const STORAGE_KEY = 'preferred-font';
	const FONTS = ['font-sans', 'font-dyslexic'] as const;
	type Font = (typeof FONTS)[number];

	function stored(): Font {
		if (typeof localStorage === 'undefined') return 'font-sans';
		const v = localStorage.getItem(STORAGE_KEY);
		return FONTS.includes(v as Font) ? (v as Font) : 'font-sans';
	}

	let font = $state<Font>(stored());

	$effect(() => {
		document.documentElement.classList.remove(...FONTS);
		document.documentElement.classList.add(font);
		localStorage.setItem(STORAGE_KEY, font);
	});
</script>

<DropdownMenu.Root>
	<DropdownMenu.Trigger>
		{#snippet child({ props })}
			<Button variant="ghost" size="icon" aria-label="Change font" {...props}>
				<TypeIcon class="size-4" />
			</Button>
		{/snippet}
	</DropdownMenu.Trigger>
	<DropdownMenu.Content align="end" class="w-48">
		<DropdownMenu.Label>Reading font</DropdownMenu.Label>
		<DropdownMenu.Separator />
		<DropdownMenu.RadioGroup bind:value={font}>
			<DropdownMenu.RadioItem value="font-sans" class="font-sans">Sora (default)</DropdownMenu.RadioItem>
			<DropdownMenu.RadioItem value="font-dyslexic" class="font-dyslexic">
				OpenDyslexic
			</DropdownMenu.RadioItem>
		</DropdownMenu.RadioGroup>
	</DropdownMenu.Content>
</DropdownMenu.Root>
