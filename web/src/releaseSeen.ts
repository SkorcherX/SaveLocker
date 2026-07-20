import { releases, releaseFor } from './releases/index';

const KEY = 'sl_last_seen_release';

/**
 * Whether there are release notes the user has not opened yet.
 *
 * Keyed on the release the RUNNING build descends from, not simply the newest release in the
 * bundle — those are the same thing, since the notes ship inside the build. A console that has
 * never been upgraded therefore never nags about a release it is not running.
 *
 * First run marks the current release as seen rather than showing a dot: a fresh install has not
 * "missed" anything, and an unread badge on first launch trains people to ignore it.
 */
export function hasUnreadNotes(version: string | undefined): boolean {
  const current = releaseFor(version) ?? releases[0];
  const seen = localStorage.getItem(KEY);
  if (seen === null) {
    localStorage.setItem(KEY, current.version);
    return false;
  }
  return seen !== current.version;
}

export function markNotesSeen(version: string | undefined): void {
  const current = releaseFor(version) ?? releases[0];
  localStorage.setItem(KEY, current.version);
}
