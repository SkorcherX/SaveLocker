# Handoff: SaveLocker Agent UI

## Overview

This is the desktop Agent UI for **SaveLocker** — a self-hosted PC game save manager. The design moves away from native Windows UI forms to a unified custom dark-mode interface modeled after modern security/agent tools (e.g. SentinelOne).

The UI is a fixed-size, app-style window rendered inside a desktop application shell (e.g. Tauri, Electron, or a WPF WebView2). It has a persistent sidebar with three navigable views: **Overview**, **Add Games**, and **Settings**.

---

## About the Design Files

`SaveLocker Agent.dc.html` is an **interactive HTML prototype** — a high-fidelity design reference, not production code. Your task is to **recreate this UI in your target codebase** (React + Tauri, WPF, Electron, etc.) using its established patterns and component libraries. Do not ship the HTML file directly.

Open the `.dc.html` file in a browser to see the fully interactive prototype. All three views are functional — click the sidebar links to switch between them.

---

## Fidelity

**High-fidelity.** This is a pixel-accurate mockup with final colors, typography, spacing, icons, and interactions. Implement it as close to pixel-perfect as your target environment allows.

---

## Design Tokens

### Colors
| Token | Hex | Usage |
|---|---|---|
| `bg-app` | `#2A3238` | Main content area background |
| `bg-surface` | `#1E252A` | Sidebar, cards, inputs, list backgrounds |
| `bg-deep` | `#0d1114` | Outer viewport background |
| `text-primary` | `#ECEFF1` | Body text, labels, button text |
| `text-muted` | `#9CA3AF` | Subtitles, paths, secondary labels, section headers |
| `accent-green` | `#129271` | Primary actions, connected status, success, active nav |
| `accent-amber` | `#f4a60d` | Destructive actions, warnings, Remove button |
| `border` | `#494949` | All borders, dividers, list separators |
| `steam-cloud-blue` | `#60a5fa` | Steam Cloud badge |
| `accent-green-dim` | `rgba(18,146,113,0.14)` | Active nav item background |
| `accent-green-glow` | `rgba(18,146,113,0.85)` | Connected status dot glow |

### Typography
- **Font family:** `Inter` (Google Fonts, weights 400/500/600/700), fallback `system-ui, -apple-system, sans-serif`
- **Monospace font:** `ui-monospace, 'Cascadia Code', Consolas, monospace` — used for paths, URLs, API keys, source badges

| Usage | Size | Weight | Color |
|---|---|---|---|
| Brand name | 13px | 700 | `#ECEFF1` |
| Brand subtitle | 10px | 400 | `#9CA3AF` |
| Nav items | 13px | 400 / 600 (active) | `#ECEFF1` / `#129271` |
| Section headers | 10px | 400 | `#9CA3AF` |
| Body / labels | 12–13px | 400–500 | `#ECEFF1` |
| Muted labels | 11–12px | 400 | `#9CA3AF` |
| Source badges | 10px mono | 400 | `#9CA3AF` |
| File paths | 10px mono | 400 | `#9CA3AF` |
| Status label | 9px | 400 | `#9CA3AF`, uppercase, ls 0.13em |
| Status value | 13px | 700 | `#129271`, ls 0.05em |

### Spacing & Shape
- **App window:** `900px × 600px`, `border-radius: 9px`
- **Sidebar width:** `212px`
- **Border radius (inputs/buttons):** `4px`
- **Border radius (list containers):** `5–6px`
- **Border radius (stat cards):** `7px`
- **Window shadow:** `0 28px 70px rgba(0,0,0,0.8), 0 0 0 1px rgba(255,255,255,0.05)`

### Icons
All icons are **Lucide** style (stroke, `strokeWidth: 1.75`, `strokeLinecap: round`, `strokeLinejoin: round`). Use `lucide-react` in your React codebase. Sizes used:
- 18px — Shield (status header)
- 14px — nav icons (Monitor, Plus, Settings)
- 13px — toolbar/action icons (RefreshCw, FolderOpen, Cloud, Server, Copy)
- 12px — footer icon (HardDrive)
- 13px — Copy, Trash2
- 40px — Cpu (Overview hero icon)

---

## App Shell

The outer shell is a viewport-filling dark background (`#0d1114`) centering the `900×600` app window. The window has a `border-radius: 9px`, `overflow: hidden`, and the box-shadow above.

The window is split into two columns via `display: flex`:
1. **Sidebar** — `212px` fixed width, `background: #1E252A`, `border-right: 1px solid #494949`
2. **Main** — `flex: 1`, `background: #2A3238`

---

## Sidebar

Layout: `flex-direction: column`, full height. Three sections:

### 1. Brand area
- `padding: 15px 14px`, `border-bottom: 1px solid #494949`
- Logo image `34×34px`, `border-radius: 5px`, `object-fit: contain`
- Text block: "SaveLocker" (13px, 700, `#ECEFF1`) + "Agent v1.0" (10px, uppercase, ls 0.07em, `#9CA3AF`)

### 2. Navigation
- `padding: 10px 8px`, `flex: 1`, `flex-direction: column`, `gap: 2px`
- Three nav items: **Overview**, **Add Games**, **Settings**

**Nav item (inactive):**
- `display: flex`, `align-items: center`, `gap: 8px`
- `padding: 8px 10px`, `border-radius: 5px`
- `background: transparent`, `color: #ECEFF1`
- `font-size: 13px`, `font-weight: 400`
- `border-left: 2px solid transparent`
- `transition: background 0.12s ease`, `cursor: pointer`

**Nav item (active):**
- `background: rgba(18,146,113,0.14)`, `color: #129271`
- `font-weight: 600`
- `border-left: 2px solid #129271`
- Icon color changes to `#129271`

Icons: Monitor (Overview), Plus (Add Games), Settings/gear (Settings) — all 14px.

### 3. Machine footer
- `padding: 11px 14px`, `border-top: 1px solid #494949`
- HardDrive icon (12px, `#9CA3AF`) + "Machine: ThunderHorse" (11px, `#9CA3AF`)
- Machine name should come from app config

---

## Persistent Status Header

Sits at the top of the main content area. Always visible regardless of active view.

- `padding: 10px 20px`, `min-height: 54px`, `background: #1E252A`
- `border-bottom: 1px solid #494949`
- `display: flex`, `justify-content: space-between`, `align-items: center`

**Left — Agent status:**
- Shield icon (18px, `#129271`) + label block
- Label: "AGENT STATUS" (9px, uppercase, ls 0.13em, `#9CA3AF`)
- Value row: green dot (7px circle, `background: #129271`, `box-shadow: 0 0 7px rgba(18,146,113,0.85)`) + "CONNECTED" text (13px, 700, `#129271`, ls 0.05em)
- When disconnected: dot color `#f4a60d`, text "DISCONNECTED" in amber

**Right — Server pill:**
- `padding: 5px 10px`, `background: rgba(18,146,113,0.07)`, `border: 1px solid rgba(18,146,113,0.2)`, `border-radius: 5px`
- Server icon (13px, `#129271`) + server URL in monospace (11px, `#9CA3AF`)
- URL reads from config (default: `localhost:5173`)

---

## View: Overview

Centered vertically and horizontally in the content area.

**Hero icon:** Cpu, 40px, `#129271`

**Title:** "Agent Running" — 18px, 700, `#ECEFF1`, ls -0.02em

**Subtitle:** "Monitoring save game activity on {machineName}" — 13px, `#9CA3AF`, `margin-top: 5px`

**Stat cards row:** `display: flex`, `gap: 14px`, `margin-top: 4px`

Each card:
- `background: #1E252A`, `border: 1px solid #494949`, `border-radius: 7px`
- `padding: 14px 22px`, `text-align: center`, `min-width: 112px`
- Value: 28px, 700, `font-variant-numeric: tabular-nums`, `line-height: 1`
- Label: 11px, `#9CA3AF`, `margin-top: 5px`, ls 0.02em

| Card | Value color | Example value | Label |
|---|---|---|---|
| Games Tracked | `#129271` | 3 | "Games Tracked" |
| Saves Backed Up | `#ECEFF1` | 24 | "Saves Backed Up" |
| Last Sync | `#9CA3AF` | 2m | "Last Sync" |

Values are dynamic — pull from agent state.

---

## View: Add Games (default view)

**Layout:** `flex-direction: column`, `gap: 11px`, `padding: 16px 20px`

### Instruction text
`color: #9CA3AF`, `font-size: 12px`, `line-height: 1.65`
> "Tick games to sync. Games without a known save folder need one set before enrolling."

### Toolbar
`display: flex`, `gap: 6px`, `flex-wrap: wrap`

**Secondary button style** (Rescan, Set save folder...):
- `padding: 5px 11px`, `background: transparent`
- `border: 1px solid #494949`, `border-radius: 4px`
- `color: #ECEFF1`, `font-size: 12px`
- Icon (13px, `#9CA3AF`) + text label, `gap: 5px`

**Hide Steam Cloud toggle button** (stateful):
- *Off:* same as secondary button, text/icon color `#9CA3AF`
- *On:* `background: rgba(18,146,113,0.1)`, `border: 1px solid #129271`, `color: #129271`, icon `#129271`

Buttons: **Rescan** (RefreshCw icon), **Set save folder...** (FolderOpen icon), **Hide Steam Cloud** (Cloud icon, toggleable)

### Game list container
- `background: #1E252A`, `border: 1px solid #494949`, `border-radius: 6px`
- `overflow-y: auto`, `flex: 1` (fills remaining space)

**Each game row:**
- `display: flex`, `align-items: flex-start`, `padding: 10px 13px`, `gap: 10px`
- `border-bottom: 1px solid rgba(73,73,73,0.4)`
- Checkbox (`accent-color: #129271`, 14×14px) + info block

**Info block (flex: 1, min-width: 0):**
- Name row: `display: flex`, `align-items: center`, `gap: 6px`, `flex-wrap: wrap`
  - Game name: 13px, 500, `#ECEFF1`
  - Source badge: 10px monospace, `#9CA3AF`, `background: rgba(255,255,255,0.05)`, `border: 1px solid rgba(255,255,255,0.09)`, `padding: 1px 6px`, `border-radius: 3px`
  - Steam Cloud badge (conditional): 10px, `color: #60a5fa`, `background: rgba(96,165,250,0.08)`, `border: 1px solid rgba(96,165,250,0.22)`, `padding: 1px 6px`, `border-radius: 3px`, text "Steam Cloud"
- Path (conditional, shown only if path exists): 10px monospace, `#9CA3AF`, `margin-top: 3px`, `overflow: hidden`, `text-overflow: ellipsis`, `white-space: nowrap`

**Game data shape:**
```ts
interface Game {
  id: number;
  name: string;
  source: 'SteamInstalled' | 'SaveRoot' | 'SteamShortcut' | string;
  hasSteamCloud: boolean;
  path: string; // empty string if unknown
  checked: boolean;
}
```

**Sample data (3 of 16 candidates):**
```
Clair Obscur: Expedition 33 | SteamInstalled | hasSteamCloud: true  | path: ""
Everything                  | SaveRoot       | hasSteamCloud: false | path: C:\Users\skorc\AppData\Local\Packages\Everything_zsz4vjnfw5ygy\LocalState
OCTOPATH TRAVELER 0         | SteamShortcut  | hasSteamCloud: false | path: C:\Users\skorc\AppData\Local\OCTOPATH_TRAVELER\Saved\SaveGames
```

### Footer bar
`display: flex`, `justify-content: space-between`, `align-items: center`

- Left: "Found {n} candidate(s)." — 12px, `#9CA3AF`
- Right: **Enroll selected** button — `padding: 7px 18px`, `background: #129271`, `border: none`, `border-radius: 5px`, `color: #fff`, `font-size: 13px`, `font-weight: 600`, ls 0.01em

---

## View: Settings

**Layout:** `overflow-y: auto`, `padding: 18px 20px`, `flex-direction: column`, `gap: 22px`

### Section header style (reused)
`color: #9CA3AF`, `font-size: 10px`, `text-transform: uppercase`, `letter-spacing: 0.11em`
`margin-bottom: 14px`, `padding-bottom: 8px`, `border-bottom: 1px solid #494949`

---

### Section 1: Connection

**Input field style (reused):**
- `background: #1E252A`, `border: 1px solid #494949`, `border-radius: 4px`
- `padding: 7px 10px`, `color: #ECEFF1`, `outline: none`
- Focus state: `border-color: #129271` (implement via `:focus` CSS)

**Label style:** `color: #9CA3AF`, `font-size: 11px`, `margin-bottom: 5px`, `display: block`

**Row 1 — Server URL:**
- Text input (flex: 1, monospace, 12px) bound to `serverUrl` config value (default: `http://localhost:5173`)
- **Save** button: `padding: 7px 15px`, `background: #129271`, `border: none`, `border-radius: 4px`, `color: #fff`, `font-size: 12px`, `font-weight: 600`
- **Register / Re-register** button: secondary style, `padding: 7px 11px`, `white-space: nowrap`

**Row 2 — Machine Name:**
- Text input `width: 240px`, 13px, inherits font
- Bound to machine name config (default: `ThunderHorse`)

**Row 3 — API Key:**
- Text input (flex: 1, monospace, 12px) bound to `apiKey`
- **Copy** button (stateful):
  - *Default:* secondary style, Copy icon (13px) + "Copy"
  - *Copied (2s):* `background: rgba(18,146,113,0.1)`, `border: 1px solid #129271`, `color: #129271`, icon `#129271`, text "✓ Copied"
  - Action: `navigator.clipboard.writeText(apiKey)`, revert after 2000ms

**Row 4 — Start with Windows:**
- Checkbox (`accent-color: #129271`) + label "Start with Windows (launch agent at login)"
- Label: 13px, `#ECEFF1`, `cursor: pointer`
- Bound to startup registry entry / config flag

---

### Section 2: Currently Tracked Games

List of enrolled games (those that passed the "Add Games" flow).

**List item** (same row style as Add Games, without checkboxes that need state):
- `background: #1E252A`, `border: 1px solid #494949`, `border-radius: 5px`, `overflow: hidden`
- Checkbox + game name (13px, 500, `#ECEFF1`) + path (10px mono, `#9CA3AF`)

**Action buttons row:** `display: flex`, `gap: 6px`
- **Set save folder...** — secondary style (FolderOpen icon)
- **Remove selected** — `border: 1px solid #f4a60d`, `color: #f4a60d`, `background: transparent`, `border-radius: 4px`, `padding: 6px 12px`, `font-size: 12px` (Trash2 icon, `#f4a60d`)

---

## State Management

```ts
interface AgentUIState {
  activeView: 'overview' | 'addGames' | 'settings';

  // Add Games
  candidateGames: Game[];          // from agent scan
  checkedGameIds: Set<number>;     // user-selected for enrollment
  hideSteamCloud: boolean;         // filters Steam Cloud games from list

  // Settings
  serverUrl: string;
  machineName: string;
  apiKey: string;
  startWithWindows: boolean;

  // UI ephemeral
  apiKeyCopied: boolean;           // true for 2s after copy action
}
```

### Key interactions
| Action | Trigger | Effect |
|---|---|---|
| Switch view | Sidebar nav click | `activeView` changes, content area swaps |
| Toggle game | Checkbox click | Add/remove from `checkedGameIds` |
| Hide Steam Cloud | Button toggle | Filter Steam Cloud games from list |
| Enroll selected | Button click | Call agent API to enroll `checkedGameIds`; clear selection |
| Rescan | Button click | Re-run save folder discovery; refresh `candidateGames` |
| Set save folder | Button click | Open OS folder picker; assign path to selected game |
| Save (settings) | Button click | Persist `serverUrl` to config file |
| Register / Re-register | Button click | POST to agent API `/register` with machine name |
| Copy API key | Button click | `clipboard.writeText(apiKey)`; show "✓ Copied" for 2s |
| Remove selected | Button click | Remove selected tracked games from agent config |
| Start with Windows | Checkbox | Toggle startup registry/config entry |

---

## Scrollbars (custom)
Apply globally:
```css
::-webkit-scrollbar { width: 5px; }
::-webkit-scrollbar-track { background: transparent; }
::-webkit-scrollbar-thumb { background: #3a4048; border-radius: 3px; }
::-webkit-scrollbar-thumb:hover { background: #555e66; }
```

---

## Assets

| File | Usage |
|---|---|
| `SaveLocker_Logo_crop.png` | Sidebar brand logo. 34×34px, `border-radius: 5px`, `object-fit: contain`. Use the app's bundled asset path. |

---

## Files in This Package

| File | Description |
|---|---|
| `README.md` | This document |
| `SaveLocker Agent.dc.html` | Fully interactive HTML prototype — open in any browser to inspect the design |
| `SaveLocker_Logo_crop.png` | Brand logo asset |

---

## Implementation Notes for Claude Code

1. **Open `SaveLocker Agent.dc.html` in a browser first.** Click through all three sidebar views to understand the full interactive behavior before writing any code.

2. **Icon library:** Use `lucide-react` for all icons. The prototype builds them from raw SVG paths but your React codebase should use the named exports directly (e.g. `<Shield size={18} color="#129271" strokeWidth={1.75} />`).

3. **Window sizing:** The `900×600` window is designed for a Tauri/Electron shell. If embedding in a resizable window, the sidebar should remain fixed at `212px` and the content area should flex to fill remaining space. Minimum recommended window width is 700px.

4. **Font loading:** Load `Inter` from Google Fonts or bundle it locally. This is critical — the design relies on Inter's metrics for spacing and alignment.

5. **Form persistence:** `serverUrl`, `machineName`, `apiKey`, and `startWithWindows` should be read from and written to the agent's config file (likely a JSON or TOML file in the app data directory).

6. **Candidate games scan:** The "Add Games" view displays results from a save-folder discovery scan. Wire `Rescan` to trigger a fresh scan via the agent backend API.
