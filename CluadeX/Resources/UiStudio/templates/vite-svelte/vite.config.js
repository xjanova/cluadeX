import cxLocator from './cluadex-locator.mjs'
import { defineConfig } from 'vite'
import { svelte } from '@sveltejs/vite-plugin-svelte'

// cxLocator(): dev-only source locations for CluadeX UI Studio (no effect on `vite build`).
export default defineConfig({
  plugins: [cxLocator(), svelte()],
})
