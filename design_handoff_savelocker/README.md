# Handoff: SaveLocker Dashboard

## Overview
SaveLocker is a self-hosted cloud save manager for PC games. This package contains high-fidelity HTML prototypes for two pages of the web admin dashboard: the **Games view** (main save management) and the **Configuration page** (server settings + machine/API key management).

## About the Design Files
The `.html` files in this bundle are **design references built as HTML prototypes** — they show the intended look, layout, and interactive behavior but are not production code to copy directly. The task is to **recreate these designs in your existing codebase** using its established framework, component library, and patterns. The HTML uses inline styles for layout precision; translate these to your styling system (Tailwind, CSS Modules, styled-components, etc.).

## Fidelity
**High-fidelity.** These are pixel-precise mockups with final colors, typography, spacing, and interactive states. Recreate the UI as closely as possible using your codebase's libraries.

---

## Design Tokens

### Color Palette
| Token | Hex | Usage |
|---|---|---|
| `bg-global` | `#2A3238` | Page background |
| `bg-card` | `#1E252A` | Cards, nav, section containers |
| `bg-table-header` | `#222d34` | Table `<thead>` rows |
| `bg-row-separator` | `#252e35` | Table row borders |
| `text-primary` | `#ECEFF1` | All primary text |
| `text-muted` | `#9CA3AF` | Subtitles, hints, helper text |
| `text-secondary` | `#8b9aaa` | Timestamps, meta, secondary labels |
| `text-dim` | `#556070` | Table column headers, placeholder text |
| `text-faint` | `#64748b` | API KEY label, least-important labels |
| `accent-green` | `#129271` | Primary actions, success states, active nav, badges |
| `accent-amber` | `#f4a60d` | Warnings, destructive actions (Delete), border accents |
| `accent-amber-light` | `#fdce63` | Commit hashes, version hash highlights |
| `border` | `#494949` | All card borders, button outlines, dividers |

### Typography
| Role | Family | Size | Weight |
|---|---|---|---|
| Brand / Logo text | Inter | 17px | 700 |
| Nav buttons | Inter | 12px | 500–600 |
| Page heading | Inter | 21–22px | 700, letter-spacing: -0.4px |
| Card section label | Inter | 10.5px | 600, uppercase, letter-spacing: 0.1em |
| Card title | Inter | 13px | 600 |
| Body / table cells | Inter | 13px | 400–500 |
| Small / meta | Inter | 11–12px | 400 |
| Monospace (hashes, paths, timestamps) | JetBrains Mono | 11–12px | 400–500 |

**Google Fonts import:**
```
https://fonts.googleapis.com/css2?family=JetBrains+Mono:wght@400;500&family=Inter:wght@300;400;500;600;700&display=swap
```

### Spacing & Shape
- Card border-radius: `8px`
- Button border-radius: `5px`
- Badge border-radius: `4px` (small), `3px` (version badges)
- Card padding: `18–20px`
- Table cell padding: `11px 18px`
- Table header cell padding: `8–9px 18px`
- Gap between nav items: `6px`
- Gap between content sections: `16px`

### Shadows & Borders
- All cards: `border: 1px solid #494949`
- Nav bar: `border-bottom: 1px solid #494949`
- No box shadows used — depth is achieved through background color contrast only

---

## Screens

### 1. Games Dashboard (`SaveLocker Dashboard.html`)
The primary view, showing details for a single selected game.

#### Navigation Bar
- **Height:** 72px (user-adjustable; default 72px, range 52–96px)
- **Background:** `#1E252A`, sticky, `z-index: 20`
- **Border-bottom:** `1px solid #494949`
- **Left side:** Logo image (portrait, `height: 64px; width: auto; border-radius: 6px`) + "SaveLocker" text (17px, weight 700; "Locker" portion in `#129271`)
- **Right side (left to right):**
  - "Games" button — active state: `background: #129271`, white text, `border: 1px solid #129271`
  - "Configuration" button — inactive: transparent bg, `border: 1px solid #494949`
  - API Key composite input: outer `border: 1px solid #494949; border-radius: 5px; overflow: hidden`; left label "API KEY" (`#64748b`, 10px, JetBrains Mono, `border-right: 1px solid #494949`); right `<input type="password">` (transparent bg, 160px wide)
  - "Connect" button — `background: #129271`, white text
  - "+ Add game" button — transparent, `border: 1px solid #494949`
  - "↻ Refresh" button — transparent, `border: 1px solid #494949`

#### Game Details Card
- Full-width card, `background: #1E252A`, `border: 1px solid #494949`, `border-radius: 8px`, `padding: 18px 20px`
- Internal layout: horizontal flex, `gap: 18px`
- **Box art placeholder** (left): `94×134px`, `background: #2A3238`, `border: 1px dashed #494949`, `border-radius: 6px`, centered image icon SVG in `#494949`
- **Info column** (right, flex: 1):
  - Title row (flex, wrap, `gap: 6px`, `align-items: center`):
    - Game name: 17px, weight 700, letter-spacing -0.3px
    - "in sync" badge: `border: 1px solid #129271; color: #129271`, 10px, weight 600
    - "free" badge: `border: 1px solid #494949; color: #8b9aaa`, 10px
    - "Refresh art", "Disable", "Details" buttons: `border: 1px solid #494949`, transparent bg, 10px
    - "Delete" button: `border: 1px solid #f4a60d; color: #f4a60d`, transparent bg, 10px
  - Meta line: 11.5px JetBrains Mono, `color: #8b9aaa` — "latest **[hash]** from **[machine]** at **[datetime]** · **[bytes]**" (hash in `#fdce63`, machine/datetime/bytes in `#ECEFF1`)
  - Save path row: `background: #2A3238`, `border: 1px solid #494949`, `border-radius: 5px`, `padding: 7px 10px`; folder SVG icon + path text (11px JetBrains Mono, `#8b9aaa`, `text-overflow: ellipsis`) + "Edit" button

#### Machines Table
- Card: `background: #1E252A`, `border: 1px solid #494949`, `border-radius: 8px`, `overflow: hidden`
- Card header: `padding: 11px 18px`, `border-bottom: 1px solid #494949` — section label "MACHINES" (10.5px, weight 600, `#8b9aaa`, uppercase, letter-spacing 0.1em)
- `<thead>` background: `#222d34`
- Columns: **Machine** | **Last upload (this game)** | **Last seen** | **Remote actions**
- Column headers: 11px, `#556070`, weight 500, `padding: 8px 18px`
- Row borders: `border-top: 1px solid #252e35`
- Row data: Machine name (13px, weight 500) | timestamps (11px JetBrains Mono, `#8b9aaa`) | action buttons
- **Action buttons per row:**
  - "Pull" — `border: 1px solid #494949`, transparent bg, 11px
  - "Push" — `border: 1px solid #494949`, transparent bg, 11px
  - "Sync" — `background: #129271`, no border, white text, 11px, weight 500
  - Button padding: `4px 10px`, `border-radius: 4px`, flex row `gap: 5px`

#### Recent Remote Commands Table
- Same card pattern as Machines
- Columns: **When** | **Machine** | **Action** | **Status** | **Result**
- **Status column:**
  - "Done" — `color: #129271`, weight 600
  - "Failed" — `color: #f4a60d`, weight 600
- Result column: 11.5px, `#8b9aaa`, `word-break: break-word`, `line-height: 1.6`, `max-width: 340px`
- "When" column: `white-space: nowrap`

#### Versions Table
- Same card pattern
- Columns: **Version** | **Machine** | **When** | **Size**
- Version cell: flex row, `align-items: center`, `gap: 7px`
  - Hash text: 12px JetBrains Mono, `#fdce63`
  - "Latest" badge: `background: #129271`, white, `border-radius: 3px`, 10px, weight 600, `padding: 2px 7px`
  - "Set as Latest" button: `border: 1px solid #f4a60d; color: #f4a60d`, transparent bg, 10px, `border-radius: 3px`

---

### 2. Configuration Page (`SaveLocker Configuration.html`)

#### Navigation Bar
- **Height:** 52px
- Same structure as Dashboard nav, with these differences:
  - Brand: logo (44px height) + "SaveLocker" text + "admin dashboard" label (11px, `#556070`, weight 400)
  - "Games" — inactive state (`border: 1px solid #494949`)
  - "Configuration" — **active** (`background: #129271`)
  - No "+ Add game" button

#### Page Header
- Flex row, `align-items: center`, `gap: 22px`, `padding: 4px 0 8px`
- Logo: `height: 160px; width: auto; border-radius: 10px`
- Text block: "Configuration" (22px, weight 700, letter-spacing -0.4px) + subtitle "SaveLocker · Self-hosted cloud save manager" (12px, `#9CA3AF`)

#### Server Settings Card
- Card header (flex, space-between): "Server settings" (13px, weight 600) | "SteamGridDB artwork" (11.5px, `#9CA3AF`)
- **Row 1** (flex, wrap, `align-items: center`, `gap: 10px`):
  - Label: "SteamGridDB API key:" (13px)
  - "configured" badge: `background: #129271`, white, 10px, weight 600
  - Masked key: JetBrains Mono, 12px, `#ECEFF1` — e.g. `••••••••47ec`
  - Override hint: 11.5px, `#9CA3AF`
- **Row 2** (flex, wrap, `align-items: center`, `gap: 8px`):
  - Text input: `flex: 1; min-width: 220px`, transparent bg, `border: 1px solid #494949`, `border-radius: 5px`, `padding: 7px 10px` — focus state: `border-color: #129271`
  - "Save key" button: `background: #129271`, white, weight 600
  - "Clear" button: transparent, `border: 1px solid #494949`
- Helper text below row 2: 11px, `#9CA3AF` — includes hyperlink to steamgriddb.com in `#129271`

#### Machines / API Keys Table
- Card header (flex, space-between): "Machines / API keys" (13px, weight 600) | "delete unwanted users/keys" (11.5px, `#9CA3AF`)
- Columns: **Machine** | **Registered** | **Last seen** | *(delete action, right-aligned)*
- Delete buttons: `border: 1px solid #f4a60d; color: #f4a60d`, transparent bg, `border-radius: 4px`, 11px, `padding: 4px 10px`

---

## Interactions & Behavior

### Navigation
- "Games" ↔ "Configuration" buttons navigate between the two pages
- The active page button is solid `#129271`; inactive is outlined `#494949`
- Logo in nav is a link back to the Dashboard

### Hover States
- All buttons: `opacity: 0.85` on hover, `transition: opacity 0.1s`
- Input focus: border transitions to `#129271`

### Forms
- API Key input in nav: `type="password"`, pre-filled with placeholder key
- SteamGridDB API key input: controlled text input with Save/Clear actions

### Scrollbar Styling (custom)
```css
::-webkit-scrollbar { width: 5px; height: 5px; }
::-webkit-scrollbar-track { background: #1E252A; }
::-webkit-scrollbar-thumb { background: #494949; border-radius: 3px; }
```

---

## State Management

### Dashboard
- `machines[]` — list of registered machines with last upload + last seen timestamps
- `recentCommands[]` — recent remote command history with status (Done/Failed) and result message
- `versions[]` — list of save file versions with hash, machine, timestamp, size, and `isLatest` flag

### Configuration
- `apiKeyInput` — controlled string for the SteamGridDB API key paste field
- `machines[]` — registered machines with registered + last seen timestamps
- `isKeyConfigured` (implied) — drives "configured" badge visibility

---

## Assets

| File | Usage |
|---|---|
| `assets/SaveLocker_Logo_crop.png` | **Primary logo** — portrait crop (825×1231px). Use at `height: 64px; width: auto` in the main nav, `height: 44px` in the config nav, `height: 160px` in the config page header. |
| `assets/SaveLocker_logo_original.png` | Original wide logo (2816×1536px, 16:9). Available as a reference or for other uses. |

---

## Files in This Package

| File | Description |
|---|---|
| `SaveLocker Dashboard.html` | Games / main dashboard view — HTML reference prototype |
| `SaveLocker Configuration.html` | Configuration page — HTML reference prototype |
| `assets/SaveLocker_Logo_crop.png` | Portrait logo, primary asset |
| `assets/SaveLocker_logo_original.png` | Wide/landscape original logo |
| `README.md` | This document |
