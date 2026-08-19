import type { components } from './api-types'

export type View = 'overview' | 'addGames' | 'settings'
export type LeaseWarning = components['schemas']['LeaseWarningDto']
export type AgentState = Omit<components['schemas']['AgentStateDto'],
  'gamesTracked' | 'savesBacked' | 'settleQuietSeconds'> & {
  gamesTracked: number
  savesBacked: number
  settleQuietSeconds: number
}
export type AgentVersion = components['schemas']['AgentVersionDto']
export type Candidate = Omit<components['schemas']['CandidateDto'], 'id'> & { id: number }
export type TrackedGame = components['schemas']['TrackedGameDto']
export type BrowseEntry = components['schemas']['BrowseEntry']
export type BrowseListing = components['schemas']['BrowseListing']
export type DeckyStatus = components['schemas']['DeckyStatusDto']
export type SyncActivitySnapshot = components['schemas']['ActivitySnapshotDto']
export type ActivityLogEntry = components['schemas']['ActivityLogEntryDto']
export type Activity = components['schemas']['ActivityDto']
