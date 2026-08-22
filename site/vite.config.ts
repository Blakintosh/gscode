import tailwindcss from '@tailwindcss/vite';
import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';

export default defineConfig({
	plugins: [tailwindcss(), sveltekit()],
	server: {
		// assetplace usually owns 5173 on this machine; keep the two apart.
		port: 5174,
		strictPort: false
	}
});
