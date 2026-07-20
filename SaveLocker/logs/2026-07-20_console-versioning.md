# Task: Console versioning + release notes

**Goal:** make "what is the console running, and what changed?" answerable from the console itself.

**Decisions taken (2026-07-20, with the maintainer):**
- Console shares the **repo product version** with the agent — one git tag, one changelog.
- Untagged main-push builds show as `0.3.2+5.a1b2c3d` ("dev build, 5 commits after v0.3.2").
- Release notes are **hand-written markdown, one file per release**, bundled into the console build
  AND used as the GitHub Release body. Single source of truth; notes ship with the code they describe.

## Steps

1. **Stamp the build.**
   - `docker-publish.yml`: add `fetch-depth: 0` + `fetch-tags: true` (git describe fails on the
     default shallow clone — same class as the documented MinVer trap).
   - Compute version / commit / build date; pass as Docker `build-args`.
   - Dockerfile: `ARG` -> `ENV` in the runtime stage.
   - Tag the image `:<version>` alongside `:latest`.
2. **Serve it.** Extend `/api/admin/status` with `version`, `commit`, `builtAt`.
   Regenerate `web/src/api-types.ts`; commit the `openapi.json` snapshot.
3. **Release notes registry.** `web/src/releases/` mirroring `web/src/help/index.ts` (`?raw` imports,
   typed registry, newest first). Sections: New / Fixed / Known issues.
4. **Show it in the console** (must be *easy to reference*):
   - Always-visible version string in the NavBar, clickable -> What's New.
   - Fuller block on Configuration beside the agent versions: version, commit SHA, build date,
     copy-to-clipboard. Console and fleet versions read in one place.
   - Unread dot until notes for the running version have been opened (localStorage).
5. **Backfill** notes for 0.3.0 and 0.3.2. Deliberately **no 0.3.1** (tagged, never published).
6. **`release.yml`**: swap `generate_release_notes: true` for `body_path`.

## Verify
- `curl localhost:5179/api/admin/status` returns a real version, not `dev`, when env vars are set.
- Console footer shows the version; What's New renders both releases; Configuration shows the
  build block.
- `docker build` with the build args produces an image whose `/api/admin/status` matches.

## Outcome (2026-07-20) — all six steps done

Verified: `/api/admin/status` returns a real stamped build; the NavBar chip, Console card and
What's New view all render against a live server with `SAVELOCKER_VERSION=0.3.2+5.a1b2c3d`; the
RUNNING badge lands on 0.3.2 despite the `+5` suffix; the unread dot appears and clears; `tsc
--noEmit` and `npm run build` are clean; the workflow's `git describe` parsing and the Dockerfile's
version split were run in isolation against all the shapes they must handle.

Two things found while building it:
- **`docker-publish.yml` had no `fetch-depth: 0`/`fetch-tags: true`** — `git describe` fails on a
  shallow clone. Fixed.
- **The Linux job's `generate_release_notes: true` would have overwritten** the hand-written body
  the installer job sets, since it runs second against the same release. Removed.

✅ **The Docker build was verified after merge** (run `29777099641`, merge `cbee5f7`). It was open
at the time of writing because the local daemon was not running. The first `main` push exercised it
and passed, with the predicted value matching exactly:
`SAVELOCKER_VERSION=0.3.2+1.cbee5f7`, image tagged `:0.3.2-1.cbee5f7` + `:latest`, and the split
running in-image as `NUMERIC="0.3.2"`.

⏳ Still unverified: a **running** container serving it — needs a pull on unRAID.

## Step 7 (added mid-session) — version skew warning

The mismatch warning was originally deferred; the maintainer asked for it in the same session.
`web/src/versionSkew.ts` + surfacing in `ConfigView`. Verified against a seeded fleet (console
0.3.2; agents 0.3.2 / 0.3.3 / 9.9.9-ci): the 0.3.3 machine badges NEWER THAN CONSOLE, the CI machine
badges TEST BUILD and is deliberately NOT counted as newer, the matching machine is unbadged, and
both Console-card warnings render. 24 logic cases pass, including dev-console-vs-tagged-agent
ordering (`0.3.2+5.abc` is ahead of `0.3.2`).

Only the newer-than-console direction warns. An older agent is normal and supported — CONTEXT.md's
deploy note is explicit that the container and the fleet upgrade in either order.

## Out of scope (deferred)
- Versioning `web/package.json` — stays `0.0.0`, the git tag is authoritative.
