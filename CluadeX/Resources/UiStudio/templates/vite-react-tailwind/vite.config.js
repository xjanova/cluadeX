import cxLocator from './cluadex-locator.mjs'
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// cxLocator(): dev-only source locations for CluadeX UI Studio (no effect on `vite build`).
export default defineConfig({
  plugins: [cxLocator(), react(), tailwindcss()],
})
