import v100 from './1.0.0.md?raw';
import v059 from './0.5.9.md?raw';
import v058 from './0.5.8.md?raw';
import v057 from './0.5.7.md?raw';
import v056 from './0.5.6.md?raw';
import v055 from './0.5.5.md?raw';
import v054 from './0.5.4.md?raw';
import v053 from './0.5.3.md?raw';
import v052 from './0.5.2.md?raw';
import v051 from './0.5.1.md?raw';
import v050 from './0.5.0.md?raw';
import v041 from './0.4.1.md?raw';
import v040 from './0.4.0.md?raw';
import v036 from './0.3.6.md?raw';
import v035 from './0.3.5.md?raw';
import v034 from './0.3.4.md?raw';
import v033 from './0.3.3.md?raw';
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
  { version: '1.0.0', date: '2026-10-22', content: v100 },
  { version: '0.5.9', date: '2026-08-16', content: v059 },
  { version: '0.5.8', date: '2026-08-16', content: v058 },
  { version: '0.5.7', date: '2026-08-15', content: v057 },
  { version: '0.5.6', date: '2026-08-15', content: v056 },
  { version: '0.5.5', date: '2026-08-15', content: v055 },
  { version: '0.5.4', date: '2026-08-10', content: v054 },
  { version: '0.5.3', date: '2026-08-09', content: v053 },
  { version: '0.5.2', date: '2026-08-08', content: v052 },
  { version: '0.5.1', date: '2026-08-05', content: v051 },
  { version: '0.5.0', date: '2026-07-29', content: v050 },
  { version: '0.4.1', date: '2026-07-25', content: v041 },
  { version: '0.4.0', date: '2026-07-25', content: v040 },
  { version: '0.3.6', date: '2026-07-24', content: v036 },
  { version: '0.3.5', date: '2026-07-24', content: v035 },
  { version: '0.3.4', date: '2026-07-23', content: v034 },
  { version: '0.3.3', date: '2026-07-23', content: v033 },
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
