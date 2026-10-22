import type { Activity, AgentState, AgentVersion, BrowseListing, Candidate, DeckyStatus, TrackedGame } from './types'

// The agent injects the local API token into index.html when it serves the page; the same-origin
// policy is what keeps any other page from reading it. Left as the literal placeholder under
// `vite dev`, where the proxy supplies the header instead.
const TOKEN = document
  .querySelector<HTMLMetaElement>('meta[name="savelocker-token"]')
  ?.content ?? ''

function authHeaders(extra?: HeadersInit): HeadersInit | undefined {
  if (!TOKEN || TOKEN.startsWith('__')) return extra
  return { ...(extra as Record<string, string> | undefined), 'X-SaveLocker-Token': TOKEN }
}

async function req<T>(path: string, options?: RequestInit): Promise<T> {
  const res = await fetch(path, { ...options, headers: authHeaders(options?.headers) })
  if (!res.ok) {
    const err = await res.json().catch(() => ({ error: res.statusText })) as { error?: string }
    throw new Error(err.error ?? res.statusText)
  }
  return res.json() as Promise<T>
}

function post<T = unknown>(path: string, body?: object): Promise<T> {
  return req<T>(path, {
    method: 'POST',
    headers: body ? { 'Content-Type': 'application/json' } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  })
}

export const api = {
  state: () => req<AgentState>('/api/state'),
  candidates: () => req<Candidate[]>('/api/candidates'),
  rescan: () => post<Candidate[]>('/api/candidates/rescan'),
  enroll: (ids: number[]) => post<{ enrolled: number; skipped: number }>('/api/enroll', { ids }),
  // identityCleared is true when the server URL moved to a different origin: the machine key, id
  // and TLS pin were issued by the old server and have been dropped, so this agent must register
  // or enroll again before it can sync.
  saveConfig: (body: {
    serverUrl?: string
    machineName?: string
    startWithWindows?: boolean
    settleQuietSeconds?: number
    // startWithWindows is the EFFECTIVE state read back from the platform, not what was asked for.
    // A refusal comes back as a failed request; this covers the quieter case where the entry was
    // written and then reverted underneath us.
  }) => post<{ identityCleared: boolean; startWithWindows: boolean }>('/api/config', body),
  register: (adminPassword?: string) =>
    post<{ machineName: string }>('/api/register', { adminPassword }),
  games: () => req<TrackedGame[]>('/api/games'),
  removeGame: (id: string) => post(`/api/games/${id}/remove`),
  // `confirm` accepts a folder the sanity heuristics flagged (a suspected Wine prefix, an oversized
  // folder). It never overrides the hard refusals — a drive root or a user profile is refused with
  // or without it.
  setGameFolder: (id: string, path: string, confirm = false) =>
    post(`/api/games/${id}/folder`, { path, confirm }),
  // Process names that mean the game is running. Empty means the Windows agent cannot detect it at
  // all — no lease, no exit push, no refusal to pull under a live game.
  setGameProcesses: (id: string, processNames: string[]) =>
    post(`/api/games/${id}/processes`, { processNames }),
  browse: (path?: string) =>
    req<BrowseListing>('/api/browse' + (path ? `?path=${encodeURIComponent(path)}` : '')),
  suggestedPath: (id: string) => req<{ path: string | null }>(`/api/games/${id}/suggested-path`),
  folderPick: () => post<{ path: string | null }>('/api/folder-pick'),
  candidateFolderPick: (id: number) => post<{ path: string | null }>(`/api/candidates/${id}/folder-pick`),
  candidateFolder: (id: number, path: string) => post(`/api/candidates/${id}/folder`, { path }),
  dismissLeaseWarning: (gameName: string) => post('/api/lease-warnings/dismiss', { gameName }),
  launchCommand: () => req<{ command: string | null; note: string | null }>('/api/launch-command'),
  decky: () => req<DeckyStatus>('/api/decky'),
  // What the agent's last check found. The agent decides this, not the UI: it is the host that
  // knows which platform's package the server offered and whether the version is actually newer.
  agentVersion: () => req<AgentVersion>('/api/agent-version'),
  // What is syncing right now (with byte progress for a push) plus a short rolling history.
  // Cheap — an in-memory read on the agent's side — so this can be polled far more often than state.
  activity: () => req<Activity>('/api/activity'),
  // Pull then push every tracked game, same as the tray menu's "Sync All". The response is a
  // one-line summary; progress for whichever game is mid-sync shows up on the next activity() poll.
  syncNow: () => post<{ message: string }>('/api/sync'),
}
