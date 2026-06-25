# Console Redesign Strategy

Back to [[Home]]. The plan for giving the SaveLocker admin dashboard a "fresh coat of paint" using Claude Design artifacts and modern frontend tooling.

## Status (2026-06-25)
**All phases complete except Phase 4 (technical codebase rename, deferred).**

- **Phase 1 — Design:** completed via `design_handoff_savelocker/` package (two high-fidelity
  HTML prototypes: Games dashboard + Configuration page). Design tokens, typography, layout, and
  interactive states are all specified. Approved.
- **Phase 2 — React project:** `web/` directory at repo root. Vite + React + TypeScript +
  Tailwind CSS v4. All API endpoints wired. Dev server at `http://localhost:5173` (proxies
  `/api` and `/art` to `:5179`; bound to `0.0.0.0` for LAN access). Feature parity confirmed.
  Additional views since initial build: **Audit Log** tab (`AuditView.tsx`).
- **Phase 3 — Docker integration:** **DONE (2026-06-24).** Multi-stage Dockerfile: Node stage
  runs `npm run build`, copies `dist/` into `src/Server/wwwroot/`. React dashboard baked into
  the production image. CI/CD via GitHub Actions → GHCR → Watchtower on unRAID.
- **Phase 4 — Codebase rename:** pending (namespaces, `.sln`, project files — see [[Future Work]]).

The **current console** (`src/Server/wwwroot/index.html`) is fully functional and clean:
vanilla JavaScript, hand-written CSS via custom properties, no build toolchain, static file
served by ASP.NET Core. It's great for testing/debugging. For **productization**, it needs a
professional redesign.

**Key insight:** The console is a thin client — all logic lives server-side in the REST API
(`/api/*` endpoints). A redesign is a **pure frontend swap with zero server changes.** That
separation is the thing that makes it low-risk and keeps the backend stable.

## Target stack
**React + TypeScript + Tailwind CSS + shadcn/ui**, built with **Vite** to static assets,
served by the existing ASP.NET Core static-files middleware.

Why:
- **Claude Design artifacts are React + Tailwind + shadcn/ui.** Prototyping a screen as an
  artifact means the approved design is *already* in this stack — moving it into the real
  project is copy-adapt-wire-to-API, not a complete re-implementation.
- **shadcn/ui** gives professional, accessible components (tables, dialogs, forms, toasts,
  tabs, charts) out of the box — exactly the things the current console is hand-rolling as
  HTML strings. No reinventing the wheel.
- **Vite + TypeScript** gives us a modern dev experience (hot reload, type safety, tree-shaking).
- **The backend stays unchanged.** Vite builds to `dist/` → drop into `wwwroot/` → existing
  static-files middleware serves it. The API key flow, CloudFlare Access auth, `/api/*`
  contract all stay identical. Same `appsettings`, same Docker build.

## Sequence (keeping the tool alive)

### Phase 1: Prototype freely (low risk)
**Design the new console as Claude artifacts** with mock data. This is pure design iteration:
- Full-page mockups of Games view, Configuration, detail panes, dialogs
- React components + Tailwind styling, all self-contained in artifacts
- **Zero impact on the running app.** You can test, iterate, approve designs without
  touching the code repo.

This is where the design work lives — fast feedback loop, no deployment pressure.

### Phase 2: Parallel frontend project (feature parity)
Once a direction is approved:
- **Create a new `web/` or `src/Dashboard` directory** — a Vite + React project
- The **current `index.html` keeps serving as-is** (optionally at `/legacy` or unchanged).
  Your testing/debugging tool never disappears.
- Wire components to the real API, screen by screen, using the approved designs.
  - Fetch from `/api/*` instead of mock data
  - Integrate the same error handling, auto-refresh (15s), key flow
  - Unit/integration tests validate the API contract
- **Launch the new dashboard at a new URL** (e.g. `/dashboard` or `/new`) while keeping
  the old one live. Users opt-in or get a redirect; the legacy tool is a fallback if needed.

### Phase 3: Fold the frontend build into deployment (productization)
**Only when the new dashboard reaches feature parity:**
- The frontend build becomes part of the Docker image. A multi-stage Dockerfile:
  1. Node stage: `npm run build` → produces `dist/`
  2. .NET stage: `COPY dist/ src/Server/wwwroot/`
- Update CI/CD to run the frontend build before containerizing
- This lands **naturally inside the deployment-hardening milestone** ([[Progress]],
  [[Decisions]]), which is already on the roadmap. That's the ideal moment to absorb the
  toolchain.

### Phase 4: Codebase rename (late productization)
Once everything is stable on the new frontend, a separate **technical rename**
`LocalGameSync` → `SaveLocker` (see [[Future Work]] "Productization / branding"):
namespaces, solution, projects, installer, mutex, paths, database.

**Why late:** Easier to rename when the UI is already finalized (no churn on two fronts).
The new frontend naturally targets the new names.

## What to do now
**Nothing structural.** Keep doing what you're doing:
- Thin client, logic stays server-side ✓
- API is the contract ✓

**One cheap win (optional but recommended):**
- Add **Swagger/OpenAPI** to the server. The new React app can generate a **typed API
  client** automatically (tools like `openapi-typescript` turn your endpoints into
  TypeScript types). Saves wiring and catches API drift at compile time.

## What NOT to do now
- Don't rename the codebase (`LocalGameSync` → `SaveLocker`) until the technical rename
  task is intentionally scheduled — it's deferred to productization phase.

## Open questions — resolved
- **Color scheme & branding.** Locked via design handoff: dark theme `#2A3238` bg,
  `#129271` accent green, `#f4a60d` amber for warnings/destructive, JetBrains Mono for
  hashes/timestamps. "SaveLocker" wordmark with green "Locker".
- **Additional pages?** Phase 2 ships Games + Configuration. Audit log / activity dashboard
  are stretch items in [[Future Work]].
- **Mobile-friendly?** Descoped for now — desktop-only intentionally (sidebar layout).

## React project structure (`web/`)
```
web/
  vite.config.ts          proxy /api + /art → :5179; host 0.0.0.0 for LAN access
  src/
    index.css             Tailwind v4 @theme tokens (design tokens → utilities)
    api.ts                typed fetch client — all API endpoints
    types.ts              TypeScript types for all API shapes
    App.tsx               root: load, 15s auto-refresh, view routing (games/config/audit)
    components/
      NavBar.tsx          logo, Games/Config/Audit Log tabs, API key input, Connect/Refresh
      GamesSidebar.tsx    220px left sidebar — cover art, name, status badge
      GamesView.tsx       sidebar + detail panel layout
      GameDetail.tsx      game card + Machines + Commands + Versions + per-machine save paths
      ConfigView.tsx      SteamGridDB settings card + Machines/API keys table
      AuditView.tsx       audit log table with color-coded action badges
    assets/
      SaveLocker_Logo_crop.png
```

## See also
- [[Decisions]] — the product name decision + the codebase-rename plan
- [[Future Work]] — productization / branding section
- [[Progress]] — deployment hardening milestone (where the Docker build lands)
