<script module lang="ts">
</script>

<script lang="ts">
    import { Button } from "$lib/components/ui/button";
    import * as DropdownMenu from "$lib/components/ui/dropdown-menu";
    // @ts-ignore
    import Type from "lucide-svelte/icons/type";
    import { onMount } from "svelte";

    let font: string = $state(
        typeof localStorage !== 'undefined' 
            ? localStorage.getItem('preferred-font') || 'font-sans'
            : 'font-sans'
    );

    $effect(() => {
        // Update font class and save preference when font changes
        document.documentElement.classList.remove("font-sans", "font-serif");
        document.documentElement.classList.add(font);
        localStorage.setItem('preferred-font', font);
    })
</script>

<DropdownMenu.Root>
    <DropdownMenu.Trigger>
        {#snippet child({ props })}
            <Button variant="outline" size="icon" {...props}>
                <Type />
                <span class="sr-only">Change font</span>
            </Button>
        {/snippet}
    </DropdownMenu.Trigger>
    <DropdownMenu.Content>
        <DropdownMenu.Group>
            <DropdownMenu.GroupHeading>
                Change font
            </DropdownMenu.GroupHeading>
            <DropdownMenu.Separator />
            <DropdownMenu.RadioGroup bind:value={font}>
                <DropdownMenu.RadioItem value="font-sans" class="font-sans">Sans (default)</DropdownMenu.RadioItem>
                <DropdownMenu.RadioItem value="font-serif" class="font-serif">Serif</DropdownMenu.RadioItem>
                <DropdownMenu.RadioItem value="font-dyslexic" class="font-dyslexic">Dyslexic</DropdownMenu.RadioItem>
            </DropdownMenu.RadioGroup>
        </DropdownMenu.Group>
    </DropdownMenu.Content>
</DropdownMenu.Root>