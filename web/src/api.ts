import type { GameSummary, Machine, Command, Conflict, Settings, Version } from './types';

let apiKey = localStorage.getItem('sl_key') || '';

export function getApiKey() { return apiKey; }
export function setApiKey(k: string) {
  apiKey = k;
  localStorage.setItem('sl_key', k);
}

function headers(extra: Record<string, string> = {}): Record<string, string> {
  return { 'X-Api-Key': apiKey, ...extra };
}

async function request<T>(path: string, opts: RequestInit = {}): Promise<T> {
  const res = await fetch('/api' + path, {
    ...opts,
    headers: { ...headers(), ...(opts.headers as Record<string, string> || {}) },
  });
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
  const ct = res.headers.get('content-type') || '';
  return ct.includes('json') ? res.json() : res.text() as unknown as T;
}

export const api = {
  overview: () => request<GameSummary[]>('/overview'),
  conflicts: () => request<Conflict[]>('/conflicts'),
  machines: () => request<Machine[]>('/machines'),
  commands: () => request<Command[]>('/commands'),
  settings: () => request<Settings>('/settings'),
  versions: (gameId: string) => request<Version[]>(`/games/${gameId}/versions`),

  refreshArt: (gameId: string) => request<{ message?: string }>(`/games/${gameId}/art/refresh`, { method: 'POST' }),
  setEnabled: (gameId: string, value: boolean) => request<void>(`/games/${gameId}/enabled?value=${value}`, { method: 'POST' }),
  deleteGame: (gameId: string) => request<void>(`/games/${gameId}`, { method: 'DELETE' }),
  addGame: (name: string, suggestedSaveDir: string | null) =>
    request<void>('/games', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name, manifestKey: null, customPathsJson: null, suggestedSaveDir }) }),
  setSaveDir: (gameId: string, value: string) => request<void>(`/games/${gameId}/save-dir?value=${encodeURIComponent(value)}`, { method: 'POST' }),
  setLatest: (gameId: string, versionId: string) => request<void>(`/games/${gameId}/set-latest?version=${versionId}`, { method: 'POST' }),
  forceRelease: (gameId: string) => request<void>(`/games/${gameId}/lease/force`, { method: 'DELETE' }),
  resolveConflict: (conflictId: string, versionId: string) => request<void>(`/conflicts/${conflictId}/resolve?version=${versionId}`, { method: 'POST' }),

  queueCommand: (machineId: string, gameId: string, type: string, force: boolean) =>
    request<void>('/commands', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ machineId, gameId, type, force }) }),

  deleteMachine: (machineId: string) =>
    fetch(`/api/machines/${machineId}`, { method: 'DELETE', headers: headers() })
      .then(res => { if (!res.ok) throw new Error(`${res.status}`); }),

  saveSgdbKey: (key: string | null) =>
    request<{ message?: string }>('/settings/steamgriddb-key', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ apiKey: key }) }),
};
