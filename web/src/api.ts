import type { GameSummary, Machine, Command, Conflict, Settings, Version, MachineSavePath, MachineScanCandidate, AuditEntry, AgentInstallerStatus, Enrollment, CreateEnrollmentResponse, AgentHealth, AdminStatus } from './types';

let adminPassword = localStorage.getItem('sl_password') || '';

export function getPassword() { return adminPassword; }
export function setPassword(p: string) {
  adminPassword = p;
  localStorage.setItem('sl_password', p);
}

function headers(extra: Record<string, string> = {}): Record<string, string> {
  return { 'X-Admin-Password': adminPassword, ...extra };
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
  adminStatus: () => fetch('/api/admin/status').then(r => r.json()) as Promise<AdminStatus>,
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
  setRetention: (gameId: string, value: number | null) =>
    request<void>(`/games/${gameId}/retain${value !== null ? `?value=${value}` : ''}`, { method: 'POST' }),
  setExcludes: (gameId: string, patterns: string[]) =>
    request<void>(`/games/${gameId}/excludes`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(patterns) }),
  deleteVersion: (gameId: string, versionId: string) =>
    request<void>(`/games/${gameId}/versions/${versionId}`, { method: 'DELETE' }),
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

  setAutoFetchHours: (hours: number) =>
    request<{ autoFetchHours: number }>('/settings/agent-update-auto-fetch', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ hours }),
    }),

  getGamePaths: (gameId: string) =>
    request<MachineSavePath[]>(`/games/${gameId}/paths`),
  getGamePathCandidates: (gameId: string) =>
    request<MachineScanCandidate[]>(`/games/${gameId}/path-candidates`),
  setMachinePath: (gameId: string, machineId: string, path: string) =>
    request<void>(`/games/${gameId}/paths/${machineId}?value=${encodeURIComponent(path)}`, { method: 'POST' }),
  clearMachinePath: (gameId: string, machineId: string) =>
    fetch(`/api/games/${gameId}/paths/${machineId}`, { method: 'DELETE', headers: headers() })
      .then(res => { if (!res.ok) throw new Error(`${res.status}`); }),

  audit: (limit = 200) => request<AuditEntry[]>(`/audit?limit=${limit}`),

  setAdminPassword: (password: string | null) =>
    request<{ ok: boolean; message: string }>('/admin/password', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ password }),
    }),

  // Every machine's health, including ones that have never sent a heartbeat — an agent that was
  // enrolled and never actually ran is exactly the case worth seeing.
  health: () => request<AgentHealth[]>('/admin/health'),

  // Dismiss does not fix the condition; if it is still true the agent's next report reopens it.
  dismissEvent: (id: string) =>
    fetch(`/api/admin/health/events/${id}/dismiss`, { method: 'POST', headers: headers() })
      .then(res => { if (!res.ok) throw new Error(`${res.status}`); }),

  enrollments: () => request<Enrollment[]>('/admin/enrollments'),

  // The raw token comes back exactly once, inside the policy — the server keeps only its hash.
  // Whatever the caller does with this response is the only chance to hand it to the user.
  createEnrollment: (body: {
    machineName: string | null;
    ttlMinutes: number;
    serverUrl: string | null;
    gameIds: string[] | null;
  }) =>
    request<CreateEnrollmentResponse>('/admin/enrollments', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    }),

  revokeEnrollment: (id: string) =>
    fetch(`/api/admin/enrollments/${id}`, { method: 'DELETE', headers: headers() })
      .then(res => { if (!res.ok) throw new Error(`${res.status}`); }),

  installerStatus: (): Promise<AgentInstallerStatus | null> =>
    fetch('/api/admin/agent-installer', { headers: headers() }).then(async res => {
      if (res.status === 204) return null;
      if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
      return res.json() as Promise<AgentInstallerStatus>;
    }),

  uploadInstaller: (formData: FormData, version: string): Promise<AgentInstallerStatus> =>
    fetch(`/api/admin/agent-installer?version=${encodeURIComponent(version)}`, {
      method: 'POST',
      headers: headers(), // no Content-Type — let browser set multipart boundary
      body: formData,
    }).then(async res => {
      if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
      return res.json() as Promise<AgentInstallerStatus>;
    }),

  deleteInstaller: (): Promise<void> =>
    fetch('/api/admin/agent-installer', { method: 'DELETE', headers: headers() })
      .then(res => { if (!res.ok) throw new Error(`${res.status}`); }),

  fetchInstallerFromGitHub: (): Promise<AgentInstallerStatus> =>
    fetch('/api/admin/agent-installer/fetch-github', { method: 'POST', headers: headers() })
      .then(async res => {
        if (!res.ok) {
          const detail = await res.json().then(j => j.detail).catch(() => null);
          throw new Error(detail || `${res.status} ${res.statusText}`);
        }
        return res.json() as Promise<AgentInstallerStatus>;
      }),
};
