// Wire types for the dashboard. These are thin aliases over the contract generated
// from the server's OpenAPI document (see api-types.ts) so they can never drift from
// the C# DTOs. Regenerate with `npm run gen:api` after changing the server API.
//
// NonNullable<> strips the `| null` that .NET's OpenAPI attaches to a DTO's base schema
// when the type also appears in a nullable position elsewhere; nullability at each use
// site is expressed by the containing schema (e.g. GameStateDto.head is itself nullable).
import type { components } from './api-types';

type Schemas = components['schemas'];

export type GameSummary = Schemas['GameStateDto'];
export type Game = Schemas['GameDto'];
export type Version = NonNullable<Schemas['SaveVersionDto']>;
export type Lease = NonNullable<Schemas['LeaseDto']>;
export type Machine = Schemas['MachineDto'];
export type Command = Schemas['AgentCommandDto'];
export type Conflict = NonNullable<Schemas['ConflictDto']>;
export type VersionStats = NonNullable<Schemas['VersionStatsDto']>;
export type Settings = Schemas['ServerSettingsDto'];
export type MachineSavePath = Schemas['MachineSavePathDto'];
export type MachineScanCandidate = Schemas['MachineScanCandidateDto'];
export type AuditEntry = Schemas['AuditEntryDto'];
export type AgentHealth = Schemas['AgentHealthDto'];
export type AgentEvent = Schemas['AgentEventDto'];
export type Enrollment = Schemas['EnrollmentDto'];
export type EnrollmentPolicy = Schemas['EnrollmentPolicy'];
export type CreateEnrollmentResponse = Schemas['CreateEnrollmentResponse'];
export type EffectiveServerUrl = Schemas['EffectiveServerUrl'];
export type AdminStatus = Schemas['AdminStatus'];
export type ServerBuildInfo = NonNullable<Schemas['ServerBuildInfo']>;
export type AgentInstallerStatus = Schemas['AgentInstallerStatus'];
export type InstallerHashVerification = Schemas['InstallerHashVerification'];
export type AutoFetchSchedule = NonNullable<Schemas['AutoFetchSchedule']>;

/**
 * Which agent a hosted package is for. Hand-written because the server's `AgentPlatform` is a
 * vocabulary of wire constants rather than an enum, so it has no schema of its own — but these
 * strings are exactly what `?platform=` accepts, and an absent parameter means `win-x64`.
 */
export type AgentPlatform = 'win-x64' | 'linux-x64' | 'decky-plugin';
