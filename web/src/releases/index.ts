import v032 from './0.3.2.md?raw';
import v030 from './0.3.0.md?raw';

export interface Release {
  /** Bare version, no leading "v". Must match the git tag with the "v" stripped. */
  version: string;
  /** UTC date the tag was pushed, ISO yyyy-mm-dd. Shown in the sidebar. */
  date: string;
  content: string;
}

/**
 * Hand-written release notes, newest first.
 *
 * These are the single source of truth: the same file is bundled into the console AND used as the
 * GitHub Release body (release.yml passes it as `body_path`). Because they ship inside the build,
 * the notes you are reading always describe the code that is serving them.
 *
 * There is deliberately no 0.3.1 — it was tagged but never published. See the note in 0.3.2.md.
 */
export const releases: Release[] = [
  { version: '0.3.2', date: '2026-07-20', content: v032 },
  { version: '0.3.0', date: '2026-07-19', content: v030 },
];

export const latestRelease = releases[0];

/**
 * Strips the "+{n}.{sha}" dev-build suffix to find the release a running build descends from.
 * A dev build shows the notes for the last real release, not nothing.
 */
export function releaseFor(version: string | undefined): Release | undefined {
  if (!version) return undefined;
  const base = version.split('+')[0];
  return releases.find(r => r.version === base);
}
