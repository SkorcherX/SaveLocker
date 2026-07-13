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
