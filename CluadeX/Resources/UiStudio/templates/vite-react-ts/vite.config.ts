import cxLocator from './cluadex-locator.mjs'
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// cxLocator() adds data-cx-src="file:line:col" in dev only, so CluadeX UI Studio
// can jump from any element on screen to the exact line that renders it.
export default defineConfig({
  plugins: [cxLocator(), react()],
})
