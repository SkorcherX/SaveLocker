import type { AgentState, Candidate, TrackedGame } from './types'

async function req<T>(path: string, options?: RequestInit): Promise<T> {
  const res = await fetch(path, options)
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
  saveConfig: (body: {
    serverUrl?: string
    machineName?: string
    startWithWindows?: boolean
    settleQuietSeconds?: number
  }) => post('/api/config', body),
  register: (adminPassword?: string) =>
    post<{ apiKey: string }>('/api/register', { adminPassword }),
  games: () => req<TrackedGame[]>('/api/games'),
  removeGame: (id: string) => post(`/api/games/${id}/remove`),
  setGameFolder: (id: string, path: string) => post(`/api/games/${id}/folder`, { path }),
  folderPick: () => post<{ path: string | null }>('/api/folder-pick'),
  candidateFolderPick: (id: number) => post<{ path: string | null }>(`/api/candidates/${id}/folder-pick`),
  dismissLeaseWarning: (gameName: string) => post('/api/lease-warnings/dismiss', { gameName }),
}
