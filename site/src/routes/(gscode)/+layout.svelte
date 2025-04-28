<script>
	import Header from '$components/app/header/header.svelte';
</script>

<Header/>
	
<main class="flex grow bg-background-article">
    <slot/>
</main>

<style>
	main {
		flex: 1;
		display: flex;
		flex-direction: column;
		width: 100%;
		margin: 0 auto;
		box-sizing: border-box;
		overflow-y: auto;
	}
</style>
