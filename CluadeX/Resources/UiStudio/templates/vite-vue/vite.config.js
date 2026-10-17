import cxLocator from './cluadex-locator.mjs'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// cxLocator(): dev-only source locations for CluadeX UI Studio (no effect on `vite build`).
export default defineConfig({
  plugins: [cxLocator(), vue()],
})
