# Task: Enroll the agent from the Windows installer (GUI)

**Status:** not started. Queued 2026-07-14, straight after enrollment (Phase 4) and Linux packaging
shipped, while the detail was fresh.

## Why

Enrollment exists (`Decisions.md` §4): the console mints a **single-use, ~15-minute** policy file, and
the agent trades it for its machine key with `savelocker enroll --file <policy>` — **no API key is
ever copied by hand**.

On Windows that is currently a **command-line-only** path. A user installs the agent from a GUI
installer and is then expected to open a terminal and type a path into
`"C:\Program Files\SaveLocker Agent\SaveLocker.Agent.exe" enroll --file …`. That is a bad seam in an
otherwise clicky flow, and it is the first thing a new user does.

**Goal:** a wizard page in the installer — *"Enrol this machine now?"* → browse to the enrollment
file → done. The machine appears in the console, online, before the installer window closes.

## What exists (so this is mechanical, not archaeology)

- `installer/SaveLocker.iss` — Inno Setup 6. `PrivilegesRequired=admin`, machine-wide install to
  `C:\Program Files\SaveLocker Agent`. It already has a `[Code]` (Pascal) section, so a custom wizard
  page is a normal addition, not a new capability.
- `[Run]` already launches the tray agent post-install with **`runasoriginaluser`** — read the comment
  there, it matters (see the ACL trap below).
- The agent CLI already does the work: `SaveLocker.Agent.exe enroll --file <path>`. **Do not
  reimplement enrollment in Pascal.** The installer's job is to collect a file path and shell out.
- Policy file shape: `src/Shared/Contracts.cs` → `EnrollmentPolicy` (camelCase JSON:
  `serverUrl`, `token`, `expiresAt`, `machineName`, `games`).

## Design

1. **A custom wizard page**, after the install-directory page and before "Ready to install":
   - Radio: **"Enrol this machine now (recommended)"** → file picker for the `.json`.
   - Radio: **"Skip — I'll enrol later"**, with one line saying how (`enroll --file`, or the
     Settings tab).
2. **Parse the policy in the installer and SHOW WHAT IT SAYS** before proceeding — server URL and
   machine name. This is not decoration. The policy file is **deliberately unsigned**
   (`Decisions.md` §4): the user is the trust anchor, and the threat is being pointed at a *malicious
   server* whose pull writes into save folders. Showing "You are about to join `https://…` as
   `Desktop-PC`" is the moment the user can notice it is the wrong server. Silently enrolling into
   whatever a downloaded file says is the one thing this page must not do.
3. **Validate early, fail kindly.** If the file will not parse, or `expiresAt` is in the past, say so
   *on the page* (**"This enrollment file expired at … — create a new one from the console"**) rather
   than failing at the end of the install.
4. **Run the enrol as a post-install step**, `runasoriginaluser`, non-fatal (below).
5. **`/ENROLL="C:\path\policy.json"`** command-line switch for scripted/unattended installs. Free once
   the code path exists, and it is how anyone deploying to several machines will want it.

## ⚠️ Traps — read these before writing code

### 1. The silent-install path is the agent's AUTO-UPDATE. Do not break it.
`TrayApp` updates itself by running the new installer with **`/SILENT /FORCECLOSEAPPLICATIONS
/NORESTART`** (`src/Agent/TrayApp.cs`). That is a **reinstall over an already-enrolled agent**, and it
is **device-verified** — it is the most load-bearing behaviour in the Windows agent.

- Inno does not show custom wizard pages in `/SILENT`, so the page will not appear — **but the
  post-install enrol step still runs.** It must be a no-op unless a file was actually chosen.
- **Never re-enrol a machine that already has an API key.** Re-enrolling would burn a token and
  *rotate the machine's key*, and a stale config would then be locked out of its own identity. Guard
  on: no file selected **or** `%PROGRAMDATA%\SaveLocker\config.json` already contains an `apiKey`.
- **Never clear or overwrite the existing config** on upgrade. It holds the API key, the TOFU server
  pin, and every tracked game's save-dir mapping and sync lineage.

### 2. The ACL trap: an elevated installer writing the agent's config
The installer runs **elevated**; the tray agent runs **de-elevated as the user** (which is exactly why
`[Run]` uses `runasoriginaluser`). The config lives machine-wide at
`%PROGRAMDATA%\SaveLocker\config.json` and the **agent must be able to rewrite it constantly** (tracked
games, `LastSyncedHash`, `LastSyncTime`, the settle settings…).

If the elevated installer creates that directory/file first, it may end up owned by Administrators
with the user holding only read access — and the agent would then **fail to save its own config**,
possibly silently. Today the agent creates it itself, de-elevated, so this has never bitten us.

- **Run the enrol step with `runasoriginaluser`**, so the config is created by the same user that will
  later write it — the same reasoning as the existing tray launch.
- **Verify the ACLs afterwards** on a machine where `%PROGRAMDATA%\SaveLocker` did not previously
  exist. `icacls "%PROGRAMDATA%\SaveLocker"` — the interactive user needs **Modify**.
- This is the failure that would look like "enrollment worked, then the agent forgot everything".

### 3. The token is single-use and expires in ~15 minutes
- A user who downloads the file, then installs an hour later, has an **expired** token. The server's
  message is already good ("*This enrollment file has expired. Mint a new one from the console.*") —
  **surface it, do not swallow it.**
- A failed enrol must **not fail the installation**. The agent is installed and perfectly usable; it
  just is not enrolled yet. Show the error, tell them how to retry, and exit 0.
  *(The Linux `install.sh` learned this exact lesson: auto-start was aborting the whole install when
  systemd's user bus was unavailable. Auto-anything is a bonus, never a reason to fail an install.)*

### 4. Do not log the token
The policy file holds a live credential. Do not echo it into the installer log, the agent log, or a
temp file that outlives the install. The agent's own `enroll` path already avoids this — keep it that
way by passing a **file path**, never the token itself, on a command line (a command line is visible
to other processes).

## Verify

Manual — installer behaviour cannot be driven by the existing harnesses. On a machine (or a clean VM)
where `%PROGRAMDATA%\SaveLocker` does **not** exist:

1. **Happy path:** mint a policy in the console → run the installer → choose the file → page shows the
   right server URL and machine name → install → the machine appears in **Configuration → Machines**,
   **online**, with its agent version. `whoami` shows the key.
2. **The agent can still write its config** (the ACL trap): let it run, add a game, confirm
   `config.json` is updated. Then `icacls "%PROGRAMDATA%\SaveLocker"` — user has **Modify**.
3. **Expired token:** mint a file, wait for it to expire (or hand-edit `expiresAt` into the past) →
   the page (or the post-install step) says so clearly, **and the install still succeeds**.
4. **Skip path:** choose "enrol later" → install succeeds → agent runs unenrolled, exactly as today.
5. **🚨 THE REGRESSION THAT MATTERS — silent upgrade of an ENROLLED agent.** Install + enrol, then
   run the new installer with `/SILENT /FORCECLOSEAPPLICATIONS /NORESTART` (what auto-update does).
   Assert: **no prompt**, the API key is **unchanged**, tracked games are **intact**, no token is
   burnt server-side, and the agent comes back up enrolled. Do this one *first* if time is short —
   it is the one that could break every existing Windows machine in the field.

## Out of scope

- **Enrolling from the tray UI's Settings tab** (a "browse for enrollment file" button there). Nice,
  and much easier than the installer work — but the installer is where a new user actually is. Do it
  after, if it still seems worth it.
- **Bundling a policy INTO the installer** (a pre-enrolled build). That is the *offline distribution*
  case, and it is the one place a **signed** policy would earn its keep (`Decisions.md` §4 says build
  signing *then*, not now). Not this task.
