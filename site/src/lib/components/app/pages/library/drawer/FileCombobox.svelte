<script lang="ts">
	import * as Popover from '$lib/components/ui/popover/index.js';
	import * as Command from '$lib/components/ui/command/index.js';

	type Props = {
		/** Header paths, `scripts/`-relative and sorted. */
		files: string[];
		/** The selected paths; empty means no filter. */
		value: string[];
		onValueChange: (value: string[]) => void;
	};

	let { files, value, onValueChange }: Props = $props();

	let open = $state(false);

	/** Every option starts `scripts/`; repeating that 112 times is noise, not information. */
	const shortName = (path: string) => path.replace(/^scripts\//, '');

	const label = $derived(
		value.length === 0
			? 'All files'
			: value.length === 1
				? shortName(value[0])
				: `${value.length} files`
	);

	function toggle(file: string) {
		onValueChange(
			value.includes(file) ? value.filter((entry) => entry !== file) : [...value, file]
		);
	}
</script>

<!-- The shadcn-svelte combobox pattern: a Popover anchoring a Command palette. Command filters
 the items against the search input by itself; selection toggles and keeps the popup open, and
 the built-in check indicator lights on data-checked. "All files" clears the set. -->
<Popover.Root bind:open>
	<Popover.Trigger>
		{#snippet child({ props })}
			<!-- Styled like the select trigger beside it: a recess with the teal caret. -->
			<button
				{...props}
				type="button"
				role="combobox"
				aria-expanded={open}
				aria-label="Filter by header files"
				class="chamfer chamfer-sm rimmed rimmed-recess text-foreground flex h-10 w-full min-w-0 cursor-pointer items-center justify-between gap-3 border-0 px-4 font-mono text-xs transition-[box-shadow,color] outline-none select-none"
			>
				<span class="truncate">{label}</span>
				<span aria-hidden="true" class="text-primary pointer-events-none leading-none">&#9662;</span>
			</button>
		{/snippet}
	</Popover.Trigger>
	<Popover.Content
		class="w-[var(--bits-popover-anchor-width)]"
		bodyClass="gap-0 p-0"
		align="start"
	>
		<Command.Root class="font-mono text-xs">
			<Command.Input placeholder="Search headers..." />
			<Command.List class="max-h-64">
				<Command.Empty>No header found.</Command.Empty>
				<Command.Group>
					<Command.Item
						value="__all__"
						keywords={['all', 'files']}
						onSelect={() => onValueChange([])}
						data-checked={value.length === 0 ? 'true' : undefined}
					>
						All files
					</Command.Item>
					{#each files as file (file)}
						<Command.Item
							value={file}
							onSelect={() => toggle(file)}
							data-checked={value.includes(file) ? 'true' : undefined}
						>
							<span class="truncate">{shortName(file)}</span>
						</Command.Item>
					{/each}
				</Command.Group>
			</Command.List>
		</Command.Root>
	</Popover.Content>
</Popover.Root>
