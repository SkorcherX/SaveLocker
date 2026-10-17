import { readFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

/**
 * The agent's state directory — where the local API's token lives. SAVELOCKER_STATE_ROOT mirrors the
 * agent's own override (AgentConfig.StateRoot), so pointing vite at a test agent takes the same
 * variable that started it.
 */
function stateDir(): string {
  const root = process.env.SAVELOCKER_STATE_ROOT
  if (root) return join(root, 'SaveLocker')

  return process.platform === 'win32'
    ? join(process.env.PROGRAMDATA ?? 'C:\\ProgramData', 'SaveLocker')
    : join(process.env.XDG_DATA_HOME ?? join(homedir(), '.local', 'share'), 'SaveLocker')
}

/**
 * The agent's local API requires a token that it normally injects into index.html. Under `vite dev`
 * the page is served by Vite, so the proxy reads the token off disk and adds the header instead.
 * Set SAVELOCKER_TOKEN to override (e.g. when the agent runs with a --config elsewhere).
 */
function localApiToken(): string {
  const fromEnv = process.env.SAVELOCKER_TOKEN
  if (fromEnv) return fromEnv

  try {
    return readFileSync(join(stateDir(), 'api-token'), 'utf8').trim()
  } catch {
    console.warn('[savelocker] no local api-token found — start the agent once, then restart vite')
    return ''
  }
}

/**
 * The port the agent's local API is on. SAVELOCKER_TRAY_PORT is already set when a test tray was
 * moved off 5178; the Linux daemon takes `--port` instead, so SAVELOCKER_AGENT_PORT covers that.
 */
const apiPort = process.env.SAVELOCKER_TRAY_PORT || process.env.SAVELOCKER_AGENT_PORT || '5178'

export default defineConfig(({ command }) => ({
  plugins: [react()],
  base: '/',
  server: {
    port: 5177,
    proxy: {
      '/api': {
        target: `http://localhost:${apiPort}`,
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
