<script>
	import { page } from '$app/stores';
</script>
 
<div class="block mx-auto mt-16 text-9xl font-normal">
	{$page.status}.
</div>
<div class="block text-muted-foreground mx-auto text-xl py-8 font-light">
	{#if $page.status === 404}
		That page doesn't exist.
	{:else if $page.status === 500}
		Something went wrong.
	{:else}
		{$page.error?.message}
	{/if}
</div>