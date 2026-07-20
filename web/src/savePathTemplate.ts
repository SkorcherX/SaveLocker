/**
 * Rewrite one machine's concrete save path into a Ludusavi-style template.
 *
 * A literal path cannot mean the same folder on two machines — a different user, a different
 * drive, or a Proton prefix. Handing one machine's path to another is what makes two agents
 * disagree about a game's save root, and a root that differs by even one segment makes a restore
 * nest a folder under itself and delete the correctly-placed copy. A template is the only form
 * every machine can expand to the same *logical* folder.
 *
 * This runs in the browser on a path an agent reported, so it matches on layout rather than on
 * the machine's own environment — which the console cannot see. That is why the agent-side
 * PathResolver.Tokenize() (exact, uses real token values) stays the better source when available.
 */

interface Rule {
  /** Matched case-insensitively against the start of the path. */
  readonly pattern: RegExp;
  readonly token: string;
}

// Order is significant: most specific first. `AppData\LocalLow` must beat `AppData\Local`, and
// every known folder must beat the bare `<home>` fallback, or a Documents path tokenizes as
// `<home>/Documents` and stops matching the manifest's own vocabulary.
const RULES: readonly Rule[] = [
  // --- Windows ---
  { pattern: /^[A-Z]:[\\/]Users[\\/]Public[\\/]?/i,                          token: '<winPublic>' },
  { pattern: /^[A-Z]:[\\/]Users[\\/][^\\/]+[\\/]AppData[\\/]LocalLow[\\/]?/i, token: '<winLocalAppDataLow>' },
  { pattern: /^[A-Z]:[\\/]Users[\\/][^\\/]+[\\/]AppData[\\/]Local[\\/]?/i,    token: '<winLocalAppData>' },
  { pattern: /^[A-Z]:[\\/]Users[\\/][^\\/]+[\\/]AppData[\\/]Roaming[\\/]?/i,  token: '<winAppData>' },
  { pattern: /^[A-Z]:[\\/]Users[\\/][^\\/]+[\\/]Saved Games[\\/]?/i,          token: '<winSavedGames>' },
  { pattern: /^[A-Z]:[\\/]Users[\\/][^\\/]+[\\/]Documents[\\/]?/i,            token: '<winDocuments>' },
  { pattern: /^[A-Z]:[\\/]ProgramData[\\/]?/i,                               token: '<winProgramData>' },
  { pattern: /^[A-Z]:[\\/]Windows[\\/]?/i,                                   token: '<winDir>' },
  { pattern: /^[A-Z]:[\\/]Users[\\/][^\\/]+[\\/]?/i,                          token: '<home>' },

  // --- Inside a Proton prefix, where the same tokens mean folders in the prefix ---
  { pattern: /^.*[\\/]pfx[\\/]drive_c[\\/]users[\\/]Public[\\/]?/i,                          token: '<winPublic>' },
  { pattern: /^.*[\\/]pfx[\\/]drive_c[\\/]users[\\/][^\\/]+[\\/]AppData[\\/]LocalLow[\\/]?/i, token: '<winLocalAppDataLow>' },
  { pattern: /^.*[\\/]pfx[\\/]drive_c[\\/]users[\\/][^\\/]+[\\/]AppData[\\/]Local[\\/]?/i,    token: '<winLocalAppData>' },
  { pattern: /^.*[\\/]pfx[\\/]drive_c[\\/]users[\\/][^\\/]+[\\/]AppData[\\/]Roaming[\\/]?/i,  token: '<winAppData>' },
  { pattern: /^.*[\\/]pfx[\\/]drive_c[\\/]users[\\/][^\\/]+[\\/]Saved Games[\\/]?/i,          token: '<winSavedGames>' },
  { pattern: /^.*[\\/]pfx[\\/]drive_c[\\/]users[\\/][^\\/]+[\\/]Documents[\\/]?/i,            token: '<winDocuments>' },
  { pattern: /^.*[\\/]pfx[\\/]drive_c[\\/]ProgramData[\\/]?/i,                               token: '<winProgramData>' },
  { pattern: /^.*[\\/]pfx[\\/]drive_c[\\/]users[\\/][^\\/]+[\\/]?/i,                          token: '<home>' },
];

/** True when this value is already a template rather than a concrete path. */
export function isTemplate(value: string | null | undefined): boolean {
  return !!value && value.includes('<') && value.includes('>');
}

/**
 * The template for a concrete path, or null when no known folder matches — a save on `D:\Games`
 * has no equivalent on another machine, and inventing one would be worse than declining.
 */
export function toTemplate(path: string | null | undefined): string | null {
  if (!path || isTemplate(path)) return null;

  const trimmed = path.trim().replace(/[\\/]+$/, '');
  for (const { pattern, token } of RULES) {
    const m = trimmed.match(pattern);
    if (!m) continue;
    const rest = trimmed.slice(m[0].length).replace(/\\/g, '/').replace(/^\/+/, '');
    return rest.length === 0 ? token : `${token}/${rest}`;
  }
  return null;
}
