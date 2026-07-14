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
export type Settings = Schemas['ServerSettingsDto'];
export type MachineSavePath = Schemas['MachineSavePathDto'];
export type AuditEntry = Schemas['AuditEntryDto'];
export type AgentHealth = Schemas['AgentHealthDto'];
export type AgentEvent = Schemas['AgentEventDto'];
export type Enrollment = Schemas['EnrollmentDto'];
export type EnrollmentPolicy = Schemas['EnrollmentPolicy'];
export type CreateEnrollmentResponse = Schemas['CreateEnrollmentResponse'];

// Hand-written — not in the generated api-types; run `npm run gen:api` after server update.
export interface AgentInstallerStatus {
  version: string;
  fileName: string;
  uploadedAt: string;
  sizeBytes: number;
}
