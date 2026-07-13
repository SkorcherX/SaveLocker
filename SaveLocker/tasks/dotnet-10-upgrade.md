# Task: Upgrade .NET 9 → .NET 10 (LTS)

Decision recorded in `Decisions.md → Runtime: .NET 10 LTS (locked 2026-07-13)`, which supersedes
the original `Tech stack — Single-language .NET 9`. Read it before starting.

**Execute ONE phase, verify it, then STOP and report.** Do not roll into the next phase.

---

## Why now (not "because it's newer")

| | Type | Support ends |
|---|---|---|
| **.NET 9** — current | STS | **10 Nov 2026** |
| **.NET 10** — target | **LTS** | 14 Nov 2028 |

- **.NET 9 is already in its maintenance phase**: security fixes only, no functional updates. It
  goes out of support in ~4 months. This is a deadline, not a preference.
- **.NET 10 is LTS** — three years, instead of another 18-month STS treadmill.
- **It dissolves the EF pin.** `Gotchas.md → EF Core version` says "pin EF Core to 9.0.x; 10.x
  requires net10." The *reason* for the pin is exactly what this upgrade removes. Delete that
  gotcha when the upgrade lands — do not leave a stale rule that tells a future session not to do
  the thing that was just done.
- **The safety net is at its maximum right now** (2026-07-13, straight after Linux agent Phase 3):
  Windows 10/10, Linux 10/10, fake-game harness 27/27, and a **cross-OS byte-compare running in
  CI**. Swapping the framework under all of that is precisely what those tests were built to catch.
  Doing it before Phase 4 also means Phases 4–6 get *written* on net10 rather than ported to it
  afterwards.

**Do this on its own branch and its own PR.** Do not mix it with Phase 4 feature work — if
something breaks, the whole value is that the cause is unambiguous.

---

## Also fixes: CI and dev silently use different SDKs

Surfaced by the Phase 3 PR run. `windows-latest` ships the **SDK 10** preinstalled, and
`dotnet build` picks the newest SDK unless pinned — so CI was building the `net9.0` targets with
**SDK 10.0.301** while the dev box used **9.0.315**. It worked (SDK 10 can target net9), but CI and
dev disagreeing about the toolchain is a latent trip hazard.

A `global.json` pins the SDK so **dev, CI and Docker finally agree**. Add it as part of this work.

---

## Phase 1 — Pin the SDK (do first, alone, still on net9)

Smallest possible step, and it is independently valuable even if the upgrade were abandoned.

1. Add `global.json` at the repo root pinning the SDK.
2. Decide the roll-forward policy deliberately — `latestMinor`/`latestFeature` determines whether a
   box with only a newer SDK still builds. **Check the Dockerfile**: it uses
   `mcr.microsoft.com/dotnet/sdk:9.0`, so a `global.json` that demands an SDK the image does not
   carry **breaks the Docker build**. The pin must be satisfiable by the dev box, the CI runners,
   *and* the container image.

**Verify:** `dotnet --version` at the repo root reports the pinned SDK; server + both agents build;
the Docker image still builds (`docker build -f src/Server/Dockerfile .`). CI green.

### Status: ✅ DONE (2026-07-13)
`global.json` pins `9.0.100` + `rollForward: latestFeature` — any 9.0.x SDK, never a roll-forward to
10. The Dockerfile **copies `global.json` in**, so the container is held to the same pin: if the image
tag and the pin ever disagree the build fails there loudly instead of quietly compiling with a
different SDK. Proven live — with SDK 10.0.301 installed alongside 9.0.315, `dotnet --version` at the
repo root still returned **9.0.315**.

**Also closed a CI hole:** `docker-publish.yml` only runs on push-to-main and `ci.yml` never built the
image, so **a broken Dockerfile was invisible to PRs** — it would first surface as a failed publish
*after* merge. Added a `docker-build` job (builds, publishes nothing). This upgrade touches the
Dockerfile twice, so the gap had to close first. CI green, 8/8.

**STOP.**

---

## Phase 2 — Retarget to net10 and lift the packages

1. `TargetFramework`: `net9.0` → `net10.0` in **Shared**, **Agent.Core**, **Agent.Linux**, **Server**;
   `net9.0-windows` → `net10.0-windows` in **Agent**.
2. Packages `9.0.9` → `10.0.x`:
   - `Microsoft.EntityFrameworkCore.Sqlite`, `Microsoft.EntityFrameworkCore.Design` (Server)
   - `Microsoft.AspNetCore.OpenApi` (Server)
   - `Microsoft.Extensions.FileSystemGlobbing` (Shared — this one is load-bearing for the
     **exclude-glob** behaviour; see the risk list)
   - `Swashbuckle.AspNetCore.SwaggerUI` is already on 10.2.3 (its own versioning — unrelated).
3. `Dockerfile`: `sdk:9.0` → `sdk:10.0`, `aspnet:9.0` → `aspnet:10.0`.
4. `setup-dotnet` version in `.github/workflows/ci.yml` **and** `release.yml`.
5. Update `global.json` from Phase 1 to the net10 SDK.
6. `MinVer` 5.0.0 — check it supports the new SDK.

**Verify:** full solution builds `--no-incremental`, 0 errors. Then the whole net, in order:
Windows suite **10/10** → Linux suite **10/10** → fake-game harness **27/27** → cross-OS chain
green in CI (author → roundtrip → confirm, byte-identical both ways).

### Status: ✅ DONE (2026-07-13)

Solution builds 0 errors. **Windows 10/10, Linux 10/10, fake-game harness 27/27, and the cross-OS
chain green in CI** — byte-identical both ways, so **.NET 10 did not change how saves hash or
archive**. That was the thing worth proving.

**The TFM was baked into more than the csproj files** — the publish profile, `SaveLocker.iss`,
`build-installer.ps1`, and the test scripts' output paths all hardcode it. A missed one fails as
"agent not built", not as a build error. Moved together.

**Risk #3 (FileSystemGlobbing 10.x) checked explicitly** — nothing in the suites covered globs, and
the cross-OS fixture deliberately avoids excluded extensions, so this was untested. A bare `*.log`
still excludes at **every depth** (the v0.1.5 gitignore-style behaviour) and the filtered tree hashes
identically to one that never had logs. Had this shifted, exclude globs would have changed which
files get archived → the content hash changes → a PC and a Deck manufacture spurious conflicts.

#### Security: one advisory fixed, one deliberately left
- **Fixed (introduced by this upgrade):** `Microsoft.AspNetCore.OpenApi` 10.0.9 resolves
  `Microsoft.OpenApi` **2.0.0**, vulnerable to **CVE-2026-49451** (NU1903 High). Pinned directly to
  **2.10.0** (patched in 2.7.5+). net9 did not pull this package at all.
- **NOT fixed, deliberately:** `SQLitePCLRaw.lib.e_sqlite3` — **CVE-2025-6965** (High, memory
  corruption in SQLite). **Pre-existing**: net9 shipped 2.1.10 with the same advisory. SDK 10 audits
  *transitive* packages by default, which is the only reason it started appearing. **There is no
  patched 2.x release** — the fix needs SQLite ≥ 3.50.2, i.e. SQLitePCLRaw **3.x**, a major bump of
  the native provider under EF Core. SQLite is the one component where a subtle break silently
  corrupts save data, and bundling it here would destroy the property that makes this upgrade safe:
  *if CI goes red, we know which change did it*. Tracked in `Backlog.md` as its own change.

#### Two "green means nothing" traps hit along the way
1. **A false green.** The first Linux run reported a cheerful **27/27** — against the **old net9
   code**. The WSL clone had a dirty tree, `git checkout` refused, the runner had no `set -e`, and it
   happily built and tested the wrong commit. The runner now hard-resets and **asserts the TFM is
   `net10.0` before drawing any conclusion**.
2. **The `.sh` files were not executable in git** (mode `100644` — they were committed from Windows).
   `./tests/linux/run-linux-tests.sh` fails `rc=126` on a fresh Linux clone. This also hit
   **`packaging/linux/install.sh` — the script a Steam Deck user is told to run.** Fixed with
   `git update-index --chmod=+x`. Nothing to do with net10; found because of it.

**Backlogged, not done:** `SYSLIB0060` — `Rfc2898DeriveBytes`'s constructor is obsolete on net10
(`Tokens.HashPassword`/`VerifyPassword`). A warning, not an error; the API still works. It is **admin
password hashing with zero test coverage**, and a subtle mistake locks the user out of their own
dashboard. Needs a test proving an OLD hash still verifies before the swap — not in a framework PR.

**STOP.**

---

## Phase 3 — Regenerate the API contract (expect real churn — read it, don't rubber-stamp)

.NET 10 changes OpenAPI document generation. `src/Server/openapi.json` will very likely diff, and
per `CLAUDE.md` any API change means regenerating `web/src/api-types.ts` too.

1. Regenerate `src/Server/openapi.json` and `web/src/api-types.ts` (`npm run gen:api`).
2. **Read the diff properly.** A changed schema shape, a nullability flip, or a renamed enum
   serialization is a *wire-contract change* that would break agents in the field. Cosmetic
   reordering is fine; a semantic change is a blocker, not a formality.
3. `web` and `agent-ui` must still typecheck and build.

**Verify:** `npm run build` in `web/` and `agent-ui/`; dashboard loads against the server and the
agent UI still renders on :5178.

### Status: ✅ DONE (2026-07-13)

**No wire-contract change. Zero paths added or removed.** The churn is real but presentational:

- **OpenAPI `3.0.1` → `3.1.1`.** .NET 10 emits 3.1, so `nullable: true` becomes
  `type: ["null","string"]`. Same meaning — 3.1 aligns nullability with JSON Schema.
- **The `*Dto2` duplicates are gone** (`SaveVersionDto2`, `ConflictDto2`, `LeaseDto2`, `BackupInfo2`).
  .NET 9's generator emitted **bogus duplicate schemas** when a type appeared in several contexts;
  .NET 10 dedupes. That is a **fix**, and nothing in `web/` ever referenced them.

**The one real problem, and why the fix lives in the server.** .NET 10 describes numeric types as a
union with string — `long` as `["integer","string"]` (an int64 can exceed JS's safe-integer range) and
`double` as `["number","string"]` (JSON cannot express NaN/Infinity). **Our server never does either**:
System.Text.Json writes both as JSON numbers, always. Left alone, that fiction propagated a
`number | string` into the generated TS for every size / byte-count / interval field and broke the
dashboard in **10 places** with `Operator '>' cannot be applied…`.

The tempting fix — sprinkle `Number(...)` across 10 call sites — would be coercing away a case that
**cannot occur**, and every future regeneration would reintroduce it. Instead an `AddSchemaTransformer`
in `Program.cs` strips the string arm, so the document *describes what the server actually sends*. This
changes only the emitted schema, never serialization.

**Result: the dashboard compiles completely unchanged.** `web` and `agent-ui` both build.

**STOP.**

---

## Phase 4 — Ship it

1. Docker image builds and runs; **existing `/data/savelocker.db` opens and migrates cleanly** —
   test against a *copy of the real DB*, not a fresh one. This is the step with actual data at risk.
2. Self-contained Linux publish for SteamOS (`packaging/linux/build-linux.sh`) still produces a
   working `savelocker`. **.NET 10 raised its minimum glibc** — confirm the Ubuntu 24.04 build box
   is still an acceptable floor for the Deck.
3. Windows installer builds (`installer/build-installer.ps1`) and the agent runs on a clean box.

**STOP.**

---

## Risks, in the order I actually expect them to bite

1. **The OpenAPI snapshot** (Phase 3). Most likely to produce noise; the danger is a *semantic*
   change hidden inside cosmetic churn. Read it.
2. **EF Core 10 + SQLite** (Phase 4). Existing migrations should still apply, but this is the only
   place a silent behaviour change touches **real user data**. Test on a copy of a real DB.
3. **`Microsoft.Extensions.FileSystemGlobbing` 10.x.** `SaveArchive.EnumerateRelativeFiles` depends
   on its exact matching semantics, and the depth-matching behaviour was *already* subtly wrong once
   (fixed in v0.1.5 — bare `*.log` must match at any depth, gitignore-style). If the matcher's
   semantics shift, **exclude globs change which files get archived** → the content hash changes →
   spurious conflicts across machines. The glob tests are the ones to watch.
4. **Self-contained SteamOS publish** (glibc floor).
5. **WinForms + WebView2 on `net10.0-windows`.** Low risk, but the least-tested corner, and the
   MSB3277 WindowsBase warning already lives there.

## Deferred risk: still no Steam Deck

Unchanged from the Linux agent work. The self-contained publish will be verified on WSL/Ubuntu,
**not on real SteamOS hardware**. Track it the same way — built ≠ verified.
