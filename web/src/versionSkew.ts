/**
 * Comparing agent versions against the console's own.
 *
 * Only ONE direction is a problem. An agent OLDER than the console is normal and supported —
 * agents update on their own schedule, and the deploy note in CONTEXT.md is explicit that the
 * container and the fleet can be upgraded in either order. An agent NEWER than the console is the
 * combination worth naming: it may expect routes or behaviour this server does not have, and the
 * failure that produces is an opaque HTTP error rather than anything that says "version skew".
 *
 * Fleet skew (agents disagreeing with EACH OTHER) is tracked separately: it is a different fault
 * with a different consequence — divergent saves and repeated conflicts.
 */

export interface ParsedVersion {
  /** Numeric release components, e.g. [0, 3, 2]. */
  parts: number[];
  /** Commits after the tag, from our "+{n}.{sha}" dev suffix. 0 for a clean release. */
  ahead: number;
}

/**
 * The version CI stamps into throwaway agent tarballs so a test build is impossible to mistake for
 * a release in the console. It would otherwise sort above every real version and permanently
 * report "newer than console" on any machine running a CI artifact.
 */
const CI_BUILD = /-ci\b/i;

export function isTestBuild(version: string | null | undefined): boolean {
  return !!version && CI_BUILD.test(version);
}

export function parseVersion(version: string | null | undefined): ParsedVersion | null {
  if (!version) return null;
  const [core, meta] = version.split('+');
  const parts = core.split('-')[0].split('.').map(n => Number(n));
  if (parts.length === 0 || parts.some(n => !Number.isFinite(n))) return null;
  const ahead = meta ? Number(meta.split('.')[0]) || 0 : 0;
  return { parts, ahead };
}

/** Negative when a < b, positive when a > b, 0 when equal. Missing components count as 0. */
export function compareVersions(a: ParsedVersion, b: ParsedVersion): number {
  const len = Math.max(a.parts.length, b.parts.length);
  for (let i = 0; i < len; i++) {
    const d = (a.parts[i] ?? 0) - (b.parts[i] ?? 0);
    if (d !== 0) return d;
  }
  // Same tag: the one with commits after it is ahead. This is what stops a dev console
  // (0.3.2+5.abc) from being reported as older than an agent on the plain 0.3.2 tag.
  return a.ahead - b.ahead;
}

/**
 * True when this agent is ahead of the console. Test builds are excluded — they are flagged as
 * test builds instead, which is both more accurate and more actionable.
 */
export function isNewerThanConsole(
  agentVersion: string | null | undefined,
  consoleVersion: string | null | undefined,
): boolean {
  if (isTestBuild(agentVersion)) return false;
  const agent = parseVersion(agentVersion);
  const console_ = parseVersion(consoleVersion);
  if (!agent || !console_) return false;
  return compareVersions(agent, console_) > 0;
}

export interface FleetSkew {
  /** Names of machines running an agent ahead of the console. */
  aheadOfConsole: string[];
  /** Names of machines running a CI test build. */
  testBuilds: string[];
  /** Distinct agent versions across the fleet, when they disagree. Empty when they agree. */
  mixedVersions: string[];
}

export function fleetSkew(
  consoleVersion: string | null | undefined,
  agents: { machineName: string; agentVersion?: string | null }[],
): FleetSkew {
  const reporting = agents.filter(a => a.agentVersion);
  const distinct = [...new Set(reporting.map(a => a.agentVersion!))];
  return {
    aheadOfConsole: reporting
      .filter(a => isNewerThanConsole(a.agentVersion, consoleVersion))
      .map(a => a.machineName),
    testBuilds: reporting.filter(a => isTestBuild(a.agentVersion)).map(a => a.machineName),
    // Sorted so the message is stable between renders rather than following heartbeat order.
    mixedVersions: distinct.length > 1 ? distinct.sort() : [],
  };
}
