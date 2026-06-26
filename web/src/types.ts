export interface GameSummary {
  game: Game;
  head: Version | null;
  lease: Lease | null;
  hasOpenConflict: boolean;
  totalStorageBytes: number;
}

export interface Game {
  id: string;
  name: string;
  enabled: boolean;
  gridUrl: string | null;
  suggestedSaveDir: string | null;
}

export interface Version {
  id: string;
  gameId: string;
  machineId: string;
  machineName: string;
  createdAt: string;
  size: number;
}

export interface Lease {
  holderMachineName: string | null;
}

export interface Machine {
  id: string;
  name: string;
  createdAt: string;
  lastSeen: string | null;
}

export interface Command {
  id: string;
  gameId: string;
  machineId: string;
  machineName: string;
  type: string;
  force: boolean;
  status: string;
  result: string | null;
  createdAt: string;
}

export interface Conflict {
  id: string;
  gameId: string;
  versionAId: string;
  versionBId: string;
}

export interface Settings {
  steamGridDbConfigured: boolean;
  steamGridDbKeyMasked: string | null;
  steamGridDbFromConfig: boolean;
  adminPasswordSet: boolean;
}

export interface MachineSavePath {
  machineId: string;
  machineName: string;
  savePath: string;
}

export interface AuditEntry {
  id: string;
  timestamp: string;
  machineId: string | null;
  machineName: string | null;
  gameId: string | null;
  gameName: string | null;
  action: string;
  detail: string | null;
}
