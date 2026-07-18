import { readFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

/**
 * The agent's local API requires a token that it normally injects into index.html. Under `vite dev`
 * the page is served by Vite, so the proxy reads the token off disk and adds the header instead.
 * Set SAVELOCKER_TOKEN to override (e.g. when the agent runs with a --config elsewhere).
 */
function localApiToken(): string {
  const fromEnv = process.env.SAVELOCKER_TOKEN
  if (fromEnv) return fromEnv

  const dir = process.platform === 'win32'
    ? join(process.env.PROGRAMDATA ?? 'C:\\ProgramData', 'SaveLocker')
    : join(process.env.XDG_DATA_HOME ?? join(homedir(), '.local', 'share'), 'SaveLocker')

  try {
    return readFileSync(join(dir, 'api-token'), 'utf8').trim()
  } catch {
    console.warn('[savelocker] no local api-token found — start the agent once, then restart vite')
    return ''
  }
}

export default defineConfig(({ command }) => ({
  plugins: [react()],
  base: '/',
  server: {
    port: 5177,
    proxy: {
      '/api': {
        target: 'http://localhost:5178',
        // Only read the token when actually serving — a production build has no agent to talk to.
        headers: command === 'serve' ? { 'X-SaveLocker-Token': localApiToken() } : undefined,
      },
    },
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
  },
}))
