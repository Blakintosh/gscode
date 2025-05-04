<script lang="ts">
	import { Separator } from "$components/ui/separator/index.js";
	import { ScrollArea } from "$components/ui/scroll-area/index.js";
	import { Button } from "$components/ui/button/index.js";
	import { Input } from "$lib/components/ui/input/index.js";
    import * as Sidebar from "$components/ui/sidebar/index.js";

	// @ts-ignore
	import Command from "lucide-svelte/icons/command";
	// @ts-ignore
	import LoaderCircle from "lucide-svelte/icons/loader-circle";
	// @ts-ignore
	import Search from "lucide-svelte/icons/search";

	import LanguageRadio from "./drawer/LanguageRadio.svelte";
	import type { ScrFunction, ScrLibrary } from "$lib/models/library";
	import { page } from "$app/stores";
	import { getLibrary } from "../../../../../routes/(gscode)/library/library";
	import { ApiLibrarian } from "$lib/app/library/api.svelte";

    const truncateString = (string = '', maxLength = 20) => 
		string.length > maxLength 
			? `${string.substring(0, maxLength)}…`
			: string;
    
    let librarian: ApiLibrarian = $state($page.data.librarian);

    let library: Promise<ScrLibrary> = $derived(librarian.library);

    async function onLanguageChange(value: string | undefined) {
        if(!value) {
            return;
        }
        
        librarian.languageId = value;
    }

    let searchTerm = $state("");

    let filteredData = $derived.by(async () => {
        let w = searchTerm.replace(/[.+^${}()|[\]\\]/g, '\\$&'); // regexp escape 
		const re = new RegExp(`^${w.replace(/\*/g,'.*').replace(/\?/g,'.')}$`,'i');
        const resolvedLibrary = await library;

        return { 
            entries: resolvedLibrary.api.filter((apiFunction: ScrFunction) => {
                return apiFunction.name.toLowerCase().includes(searchTerm.toLowerCase()) || re.test(apiFunction.name);
            }),
            languageId: resolvedLibrary.languageId
        };
    });

    let inputElement: HTMLInputElement | null = $state(null);
    function handleKeyDown(event: KeyboardEvent) {
        if (event.key === "k" && (event.ctrlKey || event.metaKey) && inputElement) {
            event.preventDefault();
            inputElement.focus();
        }
    }
</script>

<Sidebar.Root side="left" collapsible="offcanvas" variant="sidebar" class="absolute h-full">
    <Sidebar.Header class="px-4 py-4">
        <div class="flex flex-col gap-2 shrink-0">
            <div class="font-medium text-sm">
                Language
            </div>
            <LanguageRadio {onLanguageChange}/>
        </div>
    </Sidebar.Header>
    <Sidebar.Content class="flex flex-col gap-2 min-h-0 items-stretch grow px-4 py-4">
        <div class="font-medium text-sm">
            Functions
        </div>
    
        {#await filteredData}
            <div class="flex items-center justify-center w-full h-full grow text-sm text-muted-foreground gap-2">
                <LoaderCircle class="animate-spin w-4 h-4" />
                Loading...
            </div>
        {:then data}
            <ScrollArea class="w-full max-w-full min-h-0 grow">
                <div class="grid grid-flow-row auto-rows-max w-full">
                    {#each data.entries as apiFunction}
                        <Button variant="link" size={"sm"} class="justify-start font-normal text-muted-foreground" href={`/library/${data.languageId}/${apiFunction.name.toLowerCase()}`}>
                            {truncateString(apiFunction.name, 25)}
                        </Button>
                    {/each}
                </div>
            </ScrollArea>
        {:catch}
            <div class="flex items-center justify-center w-full h-full grow text-center text-sm text-muted-foreground">
                Something went wrong. Try reloading the page.
            </div>
        {/await}
    
        <div class="relative w-full shrink-0">
            <Search class="absolute left-2.5 top-[50%] -translate-y-[50%] h-4 w-4 text-muted-foreground pointer-events-none" />
            <Input
              type="search"
              placeholder="Search..."
              class="w-full rounded-lg bg-background pl-8 pr-12"
              bind:value={searchTerm}
              bind:ref={inputElement}
            />
            <div class="absolute right-2.5 top-[50%] -translate-y-[50%] flex items-center gap-1 rounded-md px-1 py-0.5 text-muted-foreground bg-muted text-xs pointer-events-none">
                <Command class="w-3.5 h-3.5"/>
                K
            </div>
        </div>
    </Sidebar.Content>
    
    <Sidebar.Footer class="text-muted-foreground text-xs inline-flex justify-between shrink-0 px-4 py-4">
        <!-- &copy; Blakintosh 2024 -->
        A part of the GSCode project.
        <a href="https://ko-fi.com/blakintosh" target="_blank" rel="noreferrer" class="text-primary hover:underline">
            Donate
        </a>
    </Sidebar.Footer>

    <Sidebar.Rail/>
</Sidebar.Root>


<svelte:window onkeydown={handleKeyDown} />