<script lang="ts">
	/**
	 * The documentation story: a pointer moves onto `GetWeapon`, and the hover card lands
	 * beneath it — raise surface, real shadow, 150ms, one pixel of travel along the light.
	 */
	import MousePointer2Icon from '@lucide/svelte/icons/mouse-pointer-2';
	import * as Code from '$lib/components/ui/code';
	import Brush from '$lib/components/site/Brush.svelte';
	import { reducedMotion } from '$lib/actions/reveal';

	let { active = false }: { active?: boolean } = $props();

	// 0 idle · 1 pointer travelling · 2 card shown
	let step = $state(0);
	$effect(() => {
		if (!active) return;
		if (reducedMotion()) {
			step = 2;
			return;
		}
		const a = setTimeout(() => (step = 1), 250);
		const b = setTimeout(() => (step = 2), 900);
		return () => {
			clearTimeout(a);
			clearTimeout(b);
		};
	});
</script>

<div class="relative">
	<Code.Root value="1" language="GSC" class="my-0" behind="background">
		<Code.Tabs><Code.Tab value="1">_weapon_utils.gsc</Code.Tab></Code.Tabs>
		<Code.Example value="1">
			<Code.Block code={`w_weapon = GetWeapon( weapon_name );`} />
		</Code.Example>
	</Code.Root>

	<!-- Pointer: starts bottom-right, travels to the token. -->
	<MousePointer2Icon
		aria-hidden="true"
		class="text-foreground pointer-events-none absolute z-20 size-4 fill-[var(--background)] transition-[left,top,opacity] duration-500 ease-out {step >= 1
			? 'top-[76px] left-[178px] opacity-100'
			: 'top-[150px] left-[70%] opacity-0'}"
	/>

	<div
		class="reveal relative z-10 mt-2 ml-6 max-w-[440px] sm:ml-16"
		data-in={step >= 2 ? '' : undefined}
		aria-hidden={step >= 2 ? undefined : 'true'}
	>
		<Brush surface="popover" behind="background" cut={10} rim="edge" shadow="overlay" bodyClass="px-4 py-3.5">
			<p class="font-mono text-sm leading-snug">
				<span class="text-foreground">GetWeapon</span><span class="text-muted-foreground"
					>( weaponName, attachmentName1, attachmentName2, … )</span
				>
			</p>
			<hr class="border-border my-2.5" />
			<p class="text-muted-foreground text-sm leading-normal">
				Get the requested weapon object based on game mode agnostic weapon name string.
			</p>
			<p class="type-label text-dim mt-3 mb-1.5">Parameters</p>
			<dl class="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-sm">
				<dt class="text-foreground font-mono">weaponName</dt>
				<dd class="text-muted-foreground">The name of the base weapon to return.</dd>
				<dt class="text-foreground font-mono">attachmentName1</dt>
				<dd class="text-muted-foreground">The first attachment name for the weapon.</dd>
			</dl>
		</Brush>
	</div>

	<div
		class="reveal relative z-10 mt-2 ml-6 w-fit sm:ml-16 [transition-delay:120ms]"
		data-in={step >= 2 ? '' : undefined}
		aria-hidden={step >= 2 ? undefined : 'true'}
	>
		<Brush surface="popover" behind="background" cut={7} rim="edge" shadow="overlay" bodyClass="flex items-center gap-2.5 px-3 py-2 font-mono text-sm">
			<i class="bg-primary marker"></i>
			<span><span class="text-foreground">w_weapon</span><span class="text-primary">: entity</span></span>
			<span class="text-dim">· inferred</span>
		</Brush>
	</div>
</div>
