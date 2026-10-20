import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

// The bundle is embedded into Aphelion.exe and served from https://app.aphelion.internal/.
export default defineConfig({
  plugins: [svelte()],
  base: './',
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    target: 'chrome120',
    assetsInlineLimit: 0,
    cssCodeSplit: false,
    sourcemap: false,
    chunkSizeWarningLimit: 800,
  },
  server: { port: 5178, strictPort: true },
});
