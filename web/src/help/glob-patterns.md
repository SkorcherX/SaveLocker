# Exclude patterns (glob filters)

## What exclude patterns do

Exclude patterns let you tell SaveLocker which files **not** to include in a save archive. Common uses:

- Exclude large log files (`*.log`)
- Exclude screenshot folders (`screenshots/**`)
- Exclude shader caches (`shadercache/**`)

## Syntax

SaveLocker uses gitignore-style glob matching.

| Pattern | What it matches |
|---------|----------------|
| `*.log` | Any `.log` file at **any depth** in the save directory |
| `*.tmp` | Any `.tmp` file at any depth |
| `screenshots/**` | Everything inside a `screenshots` folder at the root of the save dir |
| `cache/*.bin` | `.bin` files directly inside a `cache` folder at root |
| `**/cache/**` | Everything inside any folder named `cache`, at any depth |

**Bare patterns** (no `/`) match at any depth — `*.log` will match `saves/foo.log` and `saves/subdir/bar.log` alike.

**Anchored patterns** (contain `/`) are relative to the save directory root — `screenshots/**` only matches a `screenshots` folder at the top level of the save directory.

## Where to set them

Exclude patterns are per-game. In the dashboard:
1. Open the **Games** view and select a game.
2. Scroll to the **Exclude patterns** card in the game detail panel.
3. Add one pattern per line and save.

## Global defaults

A global default exclude list applies to all games unless overridden. Configure it in **Configuration → Global exclude patterns**.

## 200 MB upload cap

Regardless of exclude patterns, SaveLocker enforces a **200 MB cap** on archive uploads. If a save archive exceeds 200 MB after applying excludes, the upload is rejected. This usually indicates a misconfigured save path (e.g. pointing at the entire game install directory instead of just the save folder) or a missing exclude for large cache files.

## Example: typical log/cache excludes

```
*.log
*.tmp
shadercache/**
screenshots/**
*.dmp
```
