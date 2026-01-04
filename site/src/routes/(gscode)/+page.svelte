<script>
	import { onMount } from 'svelte';
	import * as Code from '$lib/components/ui/code';
	// @ts-ignore
	import Download from 'lucide-svelte/icons/download';
</script>

<svelte:head>
	<title>GSCode</title>
	<meta name="description" content="A better way to write scripts for Call of Duty" />
	<meta property="og:title" content="GSCode" />
	<meta property="og:site_name" content="gscode" />
	<meta property="og:description" content="A better way to write scripts for Call of Duty" />
	<meta property="og:image" content="/favicon.png" />
</svelte:head>

<section
	class="flex grow w-full h-full min-h-full items-stretch justify-center flex-col divide-y bg-[radial-gradient(ellipse_at_center,var(--background)_0%,var(--background)/80_50%,var(--background)_100%)]"
>
	<div class="flex items-center flex-col py-8 text-foreground">
		<div class="my-6">
			<!-- <img src="/images/gscode.png" alt="GSCode Logo" class="w-64 md:w-72 lg:w-96"/> -->
			<div class="w-64 md:w-72 lg:w-96 aspect-[2/1] bg-gscodeLight dark:bg-gscode bg-cover"></div>
		</div>
		<div class="inline-flex gap-1 mb-2">
			<h2 id="gscode-summary" class="text-base lg:text-2xl">
				A better way to write scripts for Call of Duty.
			</h2>
		</div>
		<h3 class="text-muted-foreground lg:text-xl my-2 inline-flex items-center gap-1">
			Available for
			<img src="/images/code-stable.png" alt="Visual Studio Code" class="h-4 lg:h-6 inline mx-2" />
			VS Code based IDEs
		</h3>


		<a
			href="https://marketplace.visualstudio.com/items?itemName=blakintosh.gscode"
			class="preview-button my-16 text-white lg:text-2xl text-lg"
			target="_blank"
			rel="noopener noreferrer"
		>
			<Download class="w-6 h-6" />
			<span>v1.1.0</span>
		</a>
	</div>
	<!-- <div class="flex items-center flex-col py-8 bg-background">
		<h3 class="text-3xl font-display mb-8">
			Script more intelligently
		</h3>
		<div class="flex items-stretch">
			<div class="w-[30rem] px-8 flex flex-col gap-16">
				<div class="border border-r-4 border-r-red-700 rounded-md bg-background-article px-4 py-1 text-muted-foreground text-sm">
					<h4 class="text-primary text-lg mb-2">
						Don't wait for the compiler to tell you.
					</h4>

					GSCode checks the syntax and dependencies of your script in real-time, helping you to fix errors early.

					<div class="border px-2 py-1 my-2 text-primary font-mono">
						Line 1: Unable to locate script 'scripts\shared\shrd.gsh'.
					</div>
					<div class="border px-2 py-1 my-2 text-primary font-mono">
						Line 11: ';' expected.
					</div>
				</div>
				<div class="border border-r-4 border-r-sky-500 rounded-md bg-background-article px-4 py-1 text-muted-foreground text-sm">
					<h4 class="text-primary text-lg mb-2">
						See more about your code.
					</h4>

					GSCode allows you to hover over code elements to view their types and documentation.

					<div class="border px-2 py-1 my-2 text-primary font-mono">
						Line 6: /@ int @/ current_health
					</div>
					<div class="border px-2 py-1 my-2 text-primary font-mono">
						Line 6: (property) /@ int @/ self.health
					</div>
				</div>
			</div>
			<Code.Root value={"1"} class="w-[36rem]">
				<Code.Tabs>
					<Code.Tab value={"1"}>
						_weapon_utils.gsc
					</Code.Tab>
				</Code.Tabs>
				<Code.Example value={"1"}>
					<Code.Block code={
`#insert scripts\\shared\\shrd.gsh;

function write_some_code( weapon_name ) 
{
    w_weapon = GetWeapon(weapon_name); 
	ammo = w_weapon.strtammo;
	
    current_health = self.health;

	if(current_health > 20)
	{
		self.health = current_health * 0.8;
	}
    
    return ammo
}`
					}/>
				</Code.Example>
			</Code.Root>
			<div class="w-[30rem] px-8 flex flex-col gap-16">
				<div class="border border-l-4 border-l-green-500 rounded-md bg-background-article px-4 py-1 text-muted-foreground text-sm">
					<h4 class="text-primary text-lg mb-2">
						See function documentation in-editor.
					</h4>

					GSCode synchronises with a community-led database of function documentation, so you can see what a function does and how to use it.

					<div class="border px-2 py-1 my-2 text-primary font-mono">
						Line 3: GetWeapon() Get the requested weapon object based on&hellip;
					</div>
				</div>
				<div class="border border-l-4 border-l-orange-600 rounded-md bg-background-article px-4 py-1 text-muted-foreground text-sm">
					<h4 class="text-primary text-lg mb-2">
						Stop runtime errors before they happen.
					</h4>

					GSCode analyses your variables and functions in-editor, catching incorrect usages of data types.

					<div class="border px-2 py-1 my-2 text-primary font-mono">
						Line 6: Property 'strtammo' does not exist on type 'struct weapon'.
					</div>
					<div class="border px-2 py-1 my-2 text-primary font-mono">
						Line 9: Cannot assigned value of type 'float' to field of type 'int' (are you missing a cast?)
					</div>
				</div>
			</div>
		</div>
	</div> -->
</section>

<style>
	.preview-button {
		display: inline-flex;
		gap: 1rem;
		align-items: center;
		padding: 12px 36px;
		border-radius: 24px;
		font-weight: 400;
		text-decoration: none;
		color: #fff;
		background: linear-gradient(45deg, #4813ba, #182af2);
		background-size: 200% 200%;
		animation: gradient 3s ease infinite;
		box-shadow: 0 4px 15px rgba(0, 0, 0, 0.2);
		transition: transform 0.2s;
	}

	.preview-button:hover {
		transform: translateY(-3px);
		box-shadow: 0 6px 20px rgba(0, 0, 0, 0.25);
	}

	@keyframes gradient {
		0% {
			background-position: 0% 50%;
		}
		50% {
			background-position: 100% 50%;
		}
		100% {
			background-position: 0% 50%;
		}
	}
</style>
