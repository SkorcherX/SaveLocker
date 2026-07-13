export type View = 'overview' | 'addGames' | 'settings'

export interface LeaseWarning {
  gameName: string
  holderMachine: string
}

export interface AgentState {
  connected: boolean
  currentVersion: string
  machineName: string
  serverUrl: string
  apiKey: string
  startWithWindows: boolean
  gamesTracked: number
  savesBacked: number
  lastSyncAgo: string
  leaseWarnings: LeaseWarning[]
  settleQuietSeconds: number
}

export interface Candidate {
  id: number
  name: string
  source: string
  hasSteamCloud: boolean
  path: string
}

export interface TrackedGame {
  id: string
  name: string
  path: string
}
