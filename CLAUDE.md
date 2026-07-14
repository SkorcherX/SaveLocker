# SaveLocker — Claude Instructions

## Session start (mandatory)

Read `SaveLocker/CONTEXT.md` and `SaveLocker/REPO_MAP.md` before doing anything else. Do not read any other files, ask clarifying questions, or begin work until both files are loaded. These two files define the current project state and codebase layout.

## Vault structure

The Obsidian vault is at `SaveLocker/` (repo root). It is flat by design — no deep nesting.

| File | When to read it |
|------|----------------|
| `SaveLocker/CONTEXT.md` | Every session start — project state, quick-ref commands, gotchas |
| `SaveLocker/REPO_MAP.md` | Every session start — codebase layout, auth model, key paths |
| `SaveLocker/Architecture.md` | When touching system design, data model, or sync flow |
| `SaveLocker/Decisions.md` | Before proposing a direction that might already be settled |
| `SaveLocker/Gotchas.md` | Before touching builds, paths, or the running server |
| `SaveLocker/API Reference.md` | When adding or changing server endpoints |
| `SaveLocker/Build and Run.md` | When running builds, Docker, or the test suite |
| `web/src/help/cli-reference.md` | When touching agent CLI commands (KB article; `SaveLocker/CLI Reference.md` is a stub pointing here) |
| `SaveLocker/Backlog.md` | When prioritizing what to work on next |
| `SaveLocker/tasks/*.md` | Feed one task file at a time — execute only its steps, then stop |
| `SaveLocker/logs/sessions.md` | When asked about project history |

## Task execution

When a `SaveLocker/tasks/` file exists for the current work:
1. Read the task file.
2. Execute **only** the steps listed.
3. Verify via the method specified in the task file.
4. Stop and report — do not continue to the next task unless instructed.

## Session handoff (end of session)

Before closing, update the vault so the next session starts clean:
1. Update `SaveLocker/CONTEXT.md` — current status, any new gotchas, next action.
2. Move completed `tasks/*.md` files to `SaveLocker/logs/` with a date prefix.
3. Update `SaveLocker/Backlog.md` if priorities shifted.
4. Commit the vault changes with a `Docs:` prefix commit message.

## Coding conventions

- Build server with `--no-incremental`; stop agent/server first (DLL lock).
- Dev storage is `src/Server/localstate/` — never `data/` (case-collision with `Data/`).
- Target framework is **net10.0** (LTS). EF Core tracks it at 10.0.x. The SDK is pinned in `global.json` — bump it and the Dockerfile's `sdk`/`aspnet` tags together.
- After any API change: regenerate `web/src/api-types.ts` and commit the updated `src/Server/openapi.json` snapshot.
- No comments unless the WHY is non-obvious. No trailing summaries after diffs.
