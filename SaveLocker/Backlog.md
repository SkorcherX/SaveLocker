# Backlog

Not-yet-done work only. Shipped items are indexed in `logs/shipped-2026-07.md`
(full detail in `logs/sessions.md`).

## Immediate
- **Device-verify 5e (glob filters)** once v0.1.4 installs — add `*.log` to a game, sync, confirm the log isn't in the archive and a log-only change creates no new version. (Server/dashboard side already live after Docker redeploy.)

## High priority
- **Console Help page / Knowledge Base** — a "Help" tab in the dashboard with curated articles + how-to guides so users can self-serve. Planned (not started):
  - **UI:** add a `help` view to `NavBar.tsx` (alongside Games/Configuration/Audit Log) → new `HelpView.tsx` — a left sidebar of categories/articles (styled like `GamesSidebar`), a content pane, and a client-side search box.
  - **Content model:** static Markdown bundled with the web app (curated, versioned with releases) — **no server/DB or API changes**. Put articles under `web/src/help/*.md` with a small index (title, category, slug). Render with a lightweight sanitized Markdown component (e.g. `react-markdown`; adds one npm dep).
  - **Deep-linking:** hash routes (e.g. `#help/conflicts`) so other UI can link in — notably the conflict warning in `GameDetail.tsx` gets a "Why did this happen?" link to the Conflicts article.
  - **Seed articles (from real support answers this project has produced):**
    - *Understanding sync conflicts* — trigger condition (uploader's known parent ≠ current server head = another machine advanced the head), the three upload outcomes (NoChange / fast-forward / conflict), why a "behind" machine keeps conflicting, and how to resolve (dashboard resolve → pull, or tray Force Pull). Source: `SyncService.UploadAsync`.
    - *How syncing works* — push/pull, head version, leases, pull-on-launch + push-on-exit.
    - *Best practices for multiple machines* — keep all agents on the same version; launch the game (lease + auto-pull) instead of hand-editing saves, to avoid divergence.
    - *Exclude patterns (glob filters)* — syntax + examples; bare patterns (`*.log`) match at any depth, `path/**` is anchored; global defaults; the 200 MB upload cap.
    - *Adding games & mapping save folders*, *Save retention*, *Agent auto-update / Fetch-from-GitHub*, *Troubleshooting*.
  - **Effort:** medium; pure web, server-agnostic. When picked up, spin a `tasks/00N_help_kb.md` from this spec.
- **Scheduled GitHub installer auto-poll** — follow-up to the shipped manual "Fetch latest from GitHub" button. A background service that periodically polls the GitHub Releases API and auto-fetches a newer installer (opt-in via config, e.g. `AgentUpdate:AutoFetchHours`). Mirror `LeaseSweeperService`'s `BackgroundService` + `IServiceScopeFactory` pattern; reuse `AgentInstallerService.FetchLatestFromGitHubAsync`.
- **Code-signing** — installer + exe currently unsigned. SmartScreen warns on first run for new users. Options: EV certificate or Azure Trusted Signing.

## Medium priority
- **Save-in-use safety** — auto-push on process-exit uses a 5 s quiet-period debounce. Some games write saves for several seconds after exit, risking a partial archive. Options: longer debounce, file-lock polling, or a user-configurable delay per game.
- **Registry-based saves** — the Ludusavi manifest has a `registry:` section; currently only `files:` paths are handled.
- **Multi-directory saves** — some games list multiple save paths in the manifest. The sync engine tracks one `SaveDirectory` per game; multi-dir support needs a schema change.

## Low priority / stretch
- **Agent local API → generated types** — `AgentApiServer.cs` is a raw `HttpListener` returning anonymous C# objects. Converting to ASP.NET Core minimal API would make it OpenAPI-introspectable so `agent-ui/src/types.ts` can be auto-generated (deferred from hygiene #5b — larger swing, touches WinForms STA + WebView2 lifetime).

_Dropped items (won't-do) are recorded in `logs/shipped-2026-07.md`._
