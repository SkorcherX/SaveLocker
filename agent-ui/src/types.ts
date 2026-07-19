import type { components } from './api-types'

export type View = 'overview' | 'addGames' | 'settings'
export type LeaseWarning = components['schemas']['LeaseWarningDto']
export type AgentState = Omit<components['schemas']['AgentStateDto'],
  'gamesTracked' | 'savesBacked' | 'settleQuietSeconds'> & {
  gamesTracked: number
  savesBacked: number
  settleQuietSeconds: number
}
export type Candidate = Omit<components['schemas']['CandidateDto'], 'id'> & { id: number }
export type TrackedGame = components['schemas']['TrackedGameDto']
export type BrowseEntry = components['schemas']['BrowseEntry']
export type BrowseListing = components['schemas']['BrowseListing']
