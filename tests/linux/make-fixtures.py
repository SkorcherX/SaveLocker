#!/usr/bin/env python3
"""Build the fake-game fixture tree for the Linux harness.

No Steam, no Proton, no GPU and no Deck are involved: the agent only ever reads shortcuts.vdf,
two environment variables, and the filesystem. Faking those exercises the entire real code path.

Two games, because there are two real save shapes:

  1. "Fake Prefix Game"   — writes INSIDE the Wine prefix (drive_c/users/steamuser/AppData/...).
                            Its AppID is deliberately NEGATIVE, to pin the signed/unsigned trap:
                            Steam stores the AppID signed but names compatdata/ with the unsigned
                            value. Code that uses the signed form looks for "-1234567890" and
                            silently finds nothing.
  2. "Fake Portable Game" — writes next to its own .exe. No prefix involved at all.

Plus three games staged in Heroic Games Launcher, which owns its own prefixes and does not obey
either Steam convention:

  3. "Heroic Prefix Game" — legendary (Epic), PROTON-shaped prefix (<winePrefix>/pfx, user
                            steamuser). It ALSO has a shortcuts.vdf entry, as Heroic's "Add to
                            Steam" creates — with no compatdata behind it, which is the whole bug:
                            the Steam path finds the game and can never resolve its saves.
  4. "Heroic Wine Game"   — sideloaded (what a GOG offline installer becomes), WINE-shaped prefix
                            (<winePrefix>/drive_c) whose Wine user is the Linux username, not
                            steamuser. Unresolvable by anything that assumes Steam's layout.
  5. "Heroic Linux Game"  — a native-Linux title. Must be SKIPPED: no prefix, out of scope.
  6. "Heroic Offline Game"— a prefix RENAMED on disk without updating Heroic's config, so the game's
                            files and the prefix it is configured to run in are two different
                            directories, both healthy-looking. Shipped as a swappable VARIANT of the
                            sideload library, because it is a deliberately broken machine.
  7. "MoonDeck Streamed Game" — a MoonDeck (Decky streaming plugin) shortcut. Its OWN Exe launches
                            MoonDeck's client script, so Steam never makes a compatdata prefix for
                            its own AppID — but it IS also installed locally under Proton, under the
                            real Steam AppID its LaunchOptions names. Also given a stray duplicate
                            shortcut (same name, different dead AppID, no LaunchOptions) — the shape
                            found on a real Deck where a launcher re-creates a shortcut instead of
                            updating the existing one.
"""
import json
import os
import shlex
import struct
import sys
import time

PREFIX_APPID_SIGNED = -1234567890          # what shortcuts.vdf stores
PREFIX_APPID_UNSIGNED = str(PREFIX_APPID_SIGNED & 0xFFFFFFFF)  # 3060399406 — the folder name
PORTABLE_APPID_SIGNED = 1234567890
PORTABLE_APPID_UNSIGNED = str(PORTABLE_APPID_SIGNED & 0xFFFFFFFF)
HEROIC_SHORTCUT_APPID_SIGNED = 1122334455

# A MoonDeck shortcut's OWN AppID never gets a prefix — Steam launches its streaming client script,
# never the game — so nothing creates compatdata for this one, on purpose. Deliberately not a
# numeric PREFIX of any other appid constant here: a substring `contains` check on the real prefix's
# path must not accidentally match this dead one's "no prefix at .../compatdata/<id>" line too.
MOONDECK_SHORTCUT_APPID_SIGNED = 1611110001
# A launcher re-creating the shortcut instead of updating it leaves a second one behind: same name,
# different (also dead) AppID, no LaunchOptions at all.
MOONDECK_DUP_APPID_SIGNED = 1622220002
# The REAL Steam AppID MoonDeck streams, per its LaunchOptions. Plain positive text, like an
# installed game's appmanifest — MOONDECK_STEAM_APP_ID is never signed/unsigned-encoded, so this
# needs none of the CompatDataId conversion the shortcut's own AppID does.
MOONDECK_REAL_APPID = "1633330"

# An installed Steam game's appid. Always positive — the signed/unsigned trap is a shortcuts.vdf
# problem only; an .acf stores the id as text and needs no conversion.
INSTALLED_APPID = "440900"

# Two more installed Steam games, differing from the one above ONLY in what the manifest says about
# Steam Cloud: one covered, one covered by GOG and not Steam. The Cloud flag is per-game manifest
# data, so a fixture with a single installed game cannot distinguish reading it from returning a
# constant — and the bug being pinned is exactly a constant.
CLOUD_APPID = "440901"
GOG_CLOUD_APPID = "440902"
# Installed in the second library, but its compatdata is left behind in the MAIN root — the shape of
# a game relocated to an SD card after Steam already created its prefix elsewhere.
RELOCATED_APPID = "440903"
# Installed, but has NO compatdata prefix anywhere at all — only a Steam Cloud sync folder under the
# Steam root's own userdata, which needs no Wine prefix to resolve.
CLOUD_ONLY_APPID = "440904"
# Genuinely installed AND also has a MoonDeck shortcut pointing at it (the Cyberpunk 2077 shape) —
# real Steam Cloud, so the dedupe's Cloud tie-break would wrongly favor the MoonDeck pointer if the
# MoonDeck check did not come first.
DUAL_SOURCE_APPID = "440905"
# The MoonDeck shortcut's OWN appid for the dual-source game — dead, same as any MoonDeck shortcut.
DUAL_SOURCE_MOONDECK_SHORTCUT_APPID_SIGNED = 1644440001
# Genuinely installed per Steam's OWN bookkeeping (libraryfolders.vdf's "apps" block) on a library
# that is NOT currently mounted at all — no ACF reachable anywhere, only a MoonDeck shortcut and
# Steam's persisted record that it belongs there. The Cyberpunk 2077 shape exactly: real save data
# still reachable (its compatdata never left the main root), but the install itself cannot be read.
PHANTOM_INSTALLED_APPID = "440906"
PHANTOM_INSTALLED_MOONDECK_SHORTCUT_APPID_SIGNED = 1655550001
# A Wine prefix whose "My Games" folder was created lowercase — the real Borderlands 2 bug
# (logs/2026-08-19_moondeck-save-detection.md): a relocated/recreated prefix's own Wine session
# writes whatever casing the game asks for, which need not match the manifest's exact-case template.
# No shortcuts.vdf/ACF entry needed — resolve --prefix takes the prefix path directly.
MISCASED_APPID = "440907"
# A Wine prefix where NEITHER casing on disk matches the manifest exactly — two siblings differing
# only by case, both wrong — pinning the newest-mtime tie-break Backlog.md settled on.
TIEBREAK_APPID = "440908"

# The Wine user for the wine-shaped prefix. Deliberately NOT "steamuser" — a plain Wine runner
# runs the game as the Linux user, and code that assumes steamuser resolves every token to a
# directory that does not exist.
HEROIC_WINE_USER = "deck"

# Heroic on a Deck is the Flatpak, so that is the layout under test.
HEROIC_FLATPAK_ID = "com.heroicgameslauncher.hgl"


def cstr(s: str) -> bytes:
    return s.encode("utf-8") + b"\x00"


def vdf_string(key: str, value: str) -> bytes:
    return b"\x01" + cstr(key) + cstr(value)


def vdf_int(key: str, value: int) -> bytes:
    return b"\x02" + cstr(key) + struct.pack("<i", value)


def shortcut(index: int, name: str, exe: str, start_dir: str, appid: int,
             launch_options: str | None = None) -> bytes:
    body = (
        vdf_int("appid", appid)
        + vdf_string("AppName", name)
        + vdf_string("Exe", f'"{exe}"')          # Steam quotes these
        + vdf_string("StartDir", f'"{start_dir}"')
    )
    if launch_options is not None:
        body += vdf_string("LaunchOptions", launch_options)
    return b"\x00" + cstr(str(index)) + body + b"\x08"


def build_shortcuts_vdf(entries: bytes) -> bytes:
    # Root object: 0x00 "shortcuts" <children> 0x08, plus Steam's trailing 0x08.
    return b"\x00" + cstr("shortcuts") + entries + b"\x08" + b"\x08"


def write_json(path: str, data) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2)


def build_heroic(home: str, games: str) -> dict:
    """Materialise a Heroic Flatpak config root with three installed games."""
    config_root = os.path.join(home, ".var", "app", HEROIC_FLATPAK_ID, "config", "heroic")
    heroic_games = os.path.join(games, "Heroic")
    prefixes = os.path.join(heroic_games, "Prefixes")

    # 3 — Epic/legendary, PROTON-shaped prefix. Saves under <winAppData> inside pfx/.
    epic_install = os.path.join(heroic_games, "HeroicPrefixGame")
    epic_prefix = os.path.join(prefixes, "HeroicPrefixGame")
    epic_save = os.path.join(
        epic_prefix, "pfx", "drive_c", "users", "steamuser",
        "AppData", "Roaming", "HeroicPrefixGame")
    os.makedirs(epic_save, exist_ok=True)
    os.makedirs(epic_install, exist_ok=True)

    # 4 — sideloaded, WINE-shaped prefix, non-steamuser user. Saves under <winDocuments>.
    wine_install = os.path.join(heroic_games, "HeroicWineGame")
    wine_prefix = os.path.join(prefixes, "HeroicWineGame")
    wine_save = os.path.join(
        wine_prefix, "drive_c", "users", HEROIC_WINE_USER, "Documents", "HeroicWineGame")
    os.makedirs(wine_save, exist_ok=True)
    os.makedirs(wine_install, exist_ok=True)

    # 6 — a RENAMED prefix, seen on a real Deck (2026-08-09).
    #
    # Heroic's sideload dialog seeds the prefix name from a title field that starts as the literal
    # word "Title", so an offline install lands in Prefixes/Title. Renaming that folder does not
    # update GamesConfig: Heroic finds nothing at the configured path, creates a fresh empty prefix
    # there, and runs the game in it. The old prefix keeps a full set of real saves that nothing
    # writes to any more.
    #
    # Both directories exist and both look healthy. The game must therefore get NO save path — the
    # stranded one is the more convincing of the two, and returning it would be worse than nothing.
    # No spaces: the harness ships these paths to bash through an eval'd KEY=VALUE line.
    renamed_prefix = os.path.join(prefixes, "HeroicOfflineGame")
    offline_install = os.path.join(renamed_prefix, "drive_c", "GOG Games", "OfflineGame")
    stranded_save = os.path.join(
        renamed_prefix, "drive_c", "users", HEROIC_WINE_USER, "Documents", "HeroicOfflineGame")
    os.makedirs(offline_install, exist_ok=True)
    os.makedirs(stranded_save, exist_ok=True)

    # What GamesConfig still points at: real, proton-shaped, freshly created, and empty.
    configured_prefix = os.path.join(prefixes, "Title")
    os.makedirs(
        os.path.join(configured_prefix, "pfx", "drive_c", "users", "steamuser"), exist_ok=True)

    # 5 — native Linux. Out of scope: it gets an install dir and no prefix, and must never appear.
    linux_install = os.path.join(heroic_games, "HeroicLinuxGame")
    os.makedirs(linux_install, exist_ok=True)

    write_json(os.path.join(config_root, "config.json"), {
        "defaultSettings": {
            "defaultInstallPath": heroic_games,
            "defaultWinePrefix": prefixes,
        }
    })

    write_json(os.path.join(config_root, "legendaryConfig", "legendary", "installed.json"), {
        "HeroicPrefixGame": {
            "app_name": "HeroicPrefixGame",
            "title": "Heroic Prefix Game",
            "install_path": epic_install,
            "platform": "Windows",
        },
        "HeroicLinuxGame": {
            "app_name": "HeroicLinuxGame",
            "title": "Heroic Linux Game",
            "install_path": linux_install,
            "platform": "Linux",
        },
    })

    healthy_sideload = {
        "app_name": "HeroicWineGame",
        "title": "Heroic Wine Game",
        "folder_name": wine_install,
        "install": {
            "executable": os.path.join(wine_install, "game.exe"),
            "platform": "Windows",
        },
    }
    renamed_sideload = {
        "app_name": "HeroicOfflineGame",
        "title": "Heroic Offline Game",
        "folder_name": offline_install,
        "install": {
            "executable": os.path.join(offline_install, "game.exe"),
            "platform": "Windows",
        },
    }

    # Case 6 ships as a VARIANT the harness swaps in, not as part of the default library. It is a
    # deliberately broken setup, and leaving it in place permanently would mean the suite could
    # never again assert that a healthy machine makes doctor exit 0.
    sideload_library = os.path.join(config_root, "sideload_apps", "library.json")
    write_json(sideload_library, {"games": [healthy_sideload]})
    write_json(sideload_library.replace(".json", ".renamed-prefix.json"),
               {"games": [healthy_sideload, renamed_sideload]})

    # Per-game prefixes. These are what make the two games resolvable at all — the global default
    # points at the Prefixes ROOT, which is not a prefix.
    for app, prefix, runner in (("HeroicPrefixGame", epic_prefix, "proton"),
                                ("HeroicWineGame", wine_prefix, "wine"),
                                # Still pointing at the pre-rename path — the whole point of case 6.
                                ("HeroicOfflineGame", configured_prefix, "proton")):
        write_json(os.path.join(config_root, "GamesConfig", f"{app}.json"), {
            app: {"winePrefix": prefix, "wineVersion": {"type": runner, "name": f"fake-{runner}"}}
        })

    # A native config root that exists but holds no config.json. Uninstalling Heroic leaves this
    # behind, and an empty shell must not be reported as an installation.
    os.makedirs(os.path.join(home, ".config", "heroic"), exist_ok=True)

    return {
        "config_root": config_root,
        "epic_install": epic_install,
        "epic_prefix": epic_prefix,
        "epic_save": epic_save,
        "wine_prefix": wine_prefix,
        "wine_save": wine_save,
        "linux_install": linux_install,
        "renamed_prefix": renamed_prefix,
        "configured_prefix": configured_prefix,
        "stranded_save": stranded_save,
        "sideload_library": sideload_library,
    }


def main() -> int:
    root = os.path.abspath(sys.argv[1])
    home = os.path.join(root, "home")
    steam = os.path.join(home, ".steam", "steam")
    games = os.path.join(root, "games")

    # --- Game 1: in-prefix ------------------------------------------------------------------
    prefix = os.path.join(steam, "steamapps", "compatdata", PREFIX_APPID_UNSIGNED)
    # The manifest's <winAppData> token must land exactly here.
    in_prefix_save = os.path.join(
        prefix, "pfx", "drive_c", "users", "steamuser",
        "AppData", "Roaming", "FakePrefixGame")
    os.makedirs(in_prefix_save, exist_ok=True)
    prefix_install = os.path.join(games, "FakePrefixGame")
    os.makedirs(prefix_install, exist_ok=True)

    # --- Game 2: portable (saves next to the .exe) -------------------------------------------
    portable_install = os.path.join(games, "FakePortableGame")
    portable_save = os.path.join(portable_install, "Saves")
    os.makedirs(portable_save, exist_ok=True)
    # A prefix exists for it too (Proton makes one for any shortcut), but its saves are NOT in it.
    os.makedirs(
        os.path.join(steam, "steamapps", "compatdata", PORTABLE_APPID_UNSIGNED, "pfx", "drive_c"),
        exist_ok=True)

    # --- MoonDeck-streamed game: local Proton install under a DIFFERENT appid than its shortcut ---
    # Steam never makes a prefix for the shortcut's own appid (its Exe launches MoonDeck's client
    # script, not the game) — nothing creates compatdata/MOONDECK_SHORTCUT_APPID_UNSIGNED at all.
    # The real prefix sits under the plain appid LaunchOptions names, exactly like an ordinary
    # locally-installed Proton game.
    moondeck_prefix = os.path.join(steam, "steamapps", "compatdata", MOONDECK_REAL_APPID)
    moondeck_save = os.path.join(
        moondeck_prefix, "pfx", "drive_c", "users", "steamuser",
        "AppData", "Roaming", "MoonDeckStreamedGame")
    os.makedirs(moondeck_save, exist_ok=True)
    # MoonDeck's own script directory — NOT the game's install dir. Standing in as <base> would
    # resolve template paths into this instead of the game, which is exactly the mistake to prove
    # does not happen.
    moondeck_script_dir = os.path.join(home, "homebrew", "plugins", "moondeck", "python")
    os.makedirs(moondeck_script_dir, exist_ok=True)

    # --- Game 3: an INSTALLED Steam game, in a second library -------------------------------
    # Deliberately not in the main Steam root. An installed game keeps its prefix in the library it
    # is installed in — unlike a shortcut, whose prefix always sits in the main root — so a scanner
    # that reuses the root it was handed finds the game and never finds its saves. A Deck's SD card
    # is exactly this case.
    steam_library = os.path.join(root, "sdcard", "SteamLibrary")
    installed_steamapps = os.path.join(steam_library, "steamapps")
    installed_install = os.path.join(installed_steamapps, "common", "FakeInstalledGame")
    installed_prefix = os.path.join(installed_steamapps, "compatdata", INSTALLED_APPID)
    installed_save = os.path.join(
        installed_prefix, "pfx", "drive_c", "users", "steamuser",
        "AppData", "Roaming", "FakeInstalledGame")
    os.makedirs(installed_save, exist_ok=True)
    os.makedirs(installed_install, exist_ok=True)

    with open(os.path.join(installed_steamapps, f"appmanifest_{INSTALLED_APPID}.acf"), "w") as f:
        f.write(
            '"AppState"\n{\n'
            f'\t"appid"\t\t"{INSTALLED_APPID}"\n'
            '\t"name"\t\t"Fake Installed Game"\n'
            '\t"installdir"\t\t"FakeInstalledGame"\n'
            "}\n")

    # The Cloud pair. Same shape as Game 3 in every respect the scanner can observe — same library,
    # same prefix layout, same manifest token — so the only thing that can separate them in the scan
    # output is the manifest's cloud block.
    cloud_saves = {}
    for appid, name, install in (
        (CLOUD_APPID, "Fake Cloud Game", "FakeCloudGame"),
        (GOG_CLOUD_APPID, "Fake Gog Cloud Game", "FakeGogCloudGame"),
    ):
        os.makedirs(os.path.join(installed_steamapps, "common", install), exist_ok=True)
        save = os.path.join(
            installed_steamapps, "compatdata", appid, "pfx", "drive_c", "users", "steamuser",
            "AppData", "Roaming", install)
        os.makedirs(save, exist_ok=True)
        cloud_saves[appid] = save
        with open(os.path.join(installed_steamapps, f"appmanifest_{appid}.acf"), "w") as f:
            f.write(
                '"AppState"\n{\n'
                f'\t"appid"\t\t"{appid}"\n'
                f'\t"name"\t\t"{name}"\n'
                f'\t"installdir"\t\t"{install}"\n'
                "}\n")

    # A game RELOCATED after Steam already made its prefix: the ACF (and install) are in the second
    # library like game 3 above, and the SECOND library's own compatdata EXISTS too — Steam made a
    # fresh one there — but it resolves nothing, while the ORIGINAL prefix, left behind in the main
    # root from before the move, has the real save history. Found on a real Deck (2026-08-19):
    # Borderlands 2's SD-card prefix is real (Steam re-created it) but empty of anything the manifest
    # matches. A present-but-unusable prefix must be retried against, not only a missing one — the
    # relocated prefix here is deliberately non-empty (a stray, differently-shaped folder) so a
    # fixture that only checked Directory.Exists would wrongly call this game resolved right here.
    relocated_install = os.path.join(installed_steamapps, "common", "FakeRelocatedGame")
    os.makedirs(relocated_install, exist_ok=True)
    with open(os.path.join(installed_steamapps, f"appmanifest_{RELOCATED_APPID}.acf"), "w") as f:
        f.write(
            '"AppState"\n{\n'
            f'\t"appid"\t\t"{RELOCATED_APPID}"\n'
            '\t"name"\t\t"Fake Relocated Game"\n'
            '\t"installdir"\t\t"FakeRelocatedGame"\n'
            "}\n")
    os.makedirs(os.path.join(
        installed_steamapps, "compatdata", RELOCATED_APPID, "pfx", "drive_c", "users", "steamuser",
        "AppData", "Roaming", "SomeOtherUnrelatedFolder"), exist_ok=True)
    relocated_save = os.path.join(
        steam, "steamapps", "compatdata", RELOCATED_APPID, "pfx", "drive_c", "users", "steamuser",
        "AppData", "Roaming", "FakeRelocatedGame")
    os.makedirs(relocated_save, exist_ok=True)

    # A game with NO compatdata prefix anywhere — only a Steam Cloud sync folder under the Steam
    # root's OWN userdata, a native path with nothing to do with any Wine prefix. Found on the same
    # Deck: Left 4 Dead 2, whose manifest save path is exactly this shape
    # (<root>/userdata/<storeUserId>/550/remote). Gating resolution on a prefix existing at all would
    # silently drop it even though the real save data is sitting right there.
    with open(os.path.join(installed_steamapps, f"appmanifest_{CLOUD_ONLY_APPID}.acf"), "w") as f:
        f.write(
            '"AppState"\n{\n'
            f'\t"appid"\t\t"{CLOUD_ONLY_APPID}"\n'
            '\t"name"\t\t"Fake Cloud Only Game"\n'
            '\t"installdir"\t\t"FakeCloudOnlyGame"\n'
            "}\n")
    os.makedirs(os.path.join(installed_steamapps, "common", "FakeCloudOnlyGame"), exist_ok=True)
    # Same account shortcuts.vdf uses below (vdf_dir) — a real machine has exactly one.
    cloud_only_save = os.path.join(steam, "userdata", "1234567", CLOUD_ONLY_APPID, "remote")
    os.makedirs(cloud_only_save, exist_ok=True)

    # A game that is BOTH genuinely installed (real Steam Cloud) AND has a MoonDeck shortcut pointing
    # at it — the Cyberpunk 2077 shape. Pins the tie-break: the installed copy must survive the
    # dedupe even though a SteamShortcut candidate is unconditionally HasSteamCloud: false, which
    # would otherwise win "prefer the copy Cloud doesn't cover" purely by construction.
    with open(os.path.join(installed_steamapps, f"appmanifest_{DUAL_SOURCE_APPID}.acf"), "w") as f:
        f.write(
            '"AppState"\n{\n'
            f'\t"appid"\t\t"{DUAL_SOURCE_APPID}"\n'
            '\t"name"\t\t"Fake Dual Source Game"\n'
            '\t"installdir"\t\t"FakeDualSourceGame"\n'
            "}\n")
    os.makedirs(os.path.join(installed_steamapps, "common", "FakeDualSourceGame"), exist_ok=True)
    dual_source_save = os.path.join(
        installed_steamapps, "compatdata", DUAL_SOURCE_APPID, "pfx", "drive_c", "users", "steamuser",
        "AppData", "Roaming", "FakeDualSourceGame")
    os.makedirs(dual_source_save, exist_ok=True)

    # A game installed on a library that will not exist on disk at all (see unmounted_library,
    # below) — no ACF anywhere reachable, only Steam's bookkeeping and a MoonDeck shortcut. Its
    # compatdata never left the main root, exactly like Cyberpunk 2077's real shape.
    phantom_installed_save = os.path.join(
        steam, "steamapps", "compatdata", PHANTOM_INSTALLED_APPID, "pfx", "drive_c", "users",
        "steamuser", "AppData", "Roaming", "FakePhantomInstalledGame")
    os.makedirs(phantom_installed_save, exist_ok=True)

    # A prefix whose "My Games" folder Wine wrote as "My games" — the manifest and a working prefix
    # both say "My Games". No shortcuts.vdf/ACF needed: --prefix takes this path directly, the same
    # way the very first fixture above (FakePrefixGame) is resolved on line ~238 of the test script.
    miscased_prefix = os.path.join(steam, "steamapps", "compatdata", MISCASED_APPID)
    miscased_docs = os.path.join(
        miscased_prefix, "pfx", "drive_c", "users", "steamuser", "Documents")
    miscased_save = os.path.join(miscased_docs, "My games", "FakeMiscasedGame")  # lowercase 'g'
    os.makedirs(miscased_save, exist_ok=True)

    # A prefix with TWO "My Games"-shaped siblings, neither matching the manifest's exact case —
    # unlike an exact match (which the ordinary Directory.Exists check already finds without any of
    # this fixture's help), this is the only shape that actually exercises the newest-wins tie-break.
    tiebreak_prefix = os.path.join(steam, "steamapps", "compatdata", TIEBREAK_APPID)
    tiebreak_docs = os.path.join(
        tiebreak_prefix, "pfx", "drive_c", "users", "steamuser", "Documents")
    tiebreak_older_save = os.path.join(tiebreak_docs, "my games", "FakeTiebreakGame")
    tiebreak_newer_save = os.path.join(tiebreak_docs, "MY GAMES", "FakeTiebreakGame")
    os.makedirs(tiebreak_older_save, exist_ok=True)
    os.makedirs(tiebreak_newer_save, exist_ok=True)
    # Back-date the "older" sibling explicitly rather than relying on the two makedirs calls above
    # landing in different filesystem-clock ticks, which is not guaranteed. utime() must run AFTER
    # creating the nested game subfolder, not before: POSIX bumps a directory's own mtime again when
    # an entry is added to it, so setting it first would just get overwritten by the makedirs call.
    hour_ago = time.time() - 3600
    os.utime(os.path.join(tiebreak_docs, "my games"), (hour_ago, hour_ago))

    # A runtime, not a game. Filtered by appid — it ships no toolmanifest.vdf, so it is the one
    # case the hardcoded list still has to carry.
    with open(os.path.join(installed_steamapps, "appmanifest_228980.acf"), "w") as f:
        f.write(
            '"AppState"\n{\n'
            '\t"appid"\t\t"228980"\n'
            '\t"name"\t\t"Steamworks Common Redistributables"\n'
            '\t"installdir"\t\t"Steamworks Shared"\n'
            "}\n")

    # A compat tool with an appid NOTHING can have hardcoded — that is the entire point. A Deck
    # listed Proton 9/10/11, Proton Hotfix and Steam Linux Runtime 4.0 as enrollable games because
    # the filter was a list of appids and Valve keeps minting new ones. The marker that identifies
    # it is toolmanifest.vdf in its install directory, which every compat tool has and no game does.
    tool_install = os.path.join(installed_steamapps, "common", "Proton 99.0")
    os.makedirs(tool_install, exist_ok=True)
    with open(os.path.join(tool_install, "toolmanifest.vdf"), "w") as f:
        f.write('"manifest"\n{\n\t"version"\t\t"2"\n\t"commandline"\t\t"/proton run"\n}\n')
    with open(os.path.join(installed_steamapps, "appmanifest_9999990.acf"), "w") as f:
        f.write(
            '"AppState"\n{\n'
            '\t"appid"\t\t"9999990"\n'
            '\t"name"\t\t"Proton 99.0"\n'
            '\t"installdir"\t\t"Proton 99.0"\n'
            "}\n")

    # A third library Steam still remembers (libraryfolders.vdf persists it) but that does not exist
    # on disk right now — an SD card that isn't currently inserted. Deliberately never created with
    # os.makedirs; the whole point is that it is absent. Found on a real Deck (2026-08-19): Cyberpunk
    # 2077's genuine 91 GB Steam install sat on exactly this kind of unmounted card. Its "apps" block
    # names PHANTOM_INSTALLED_APPID — Steam's own bookkeeping survives the card being absent, which is
    # the whole point: it is what lets discovery tell "genuinely installed, unreachable right now"
    # apart from "not installed at all", the same way the real SD2 card's own apps block did.
    unmounted_library = os.path.join(root, "sdcard", "UnmountedLibrary")

    os.makedirs(os.path.join(steam, "steamapps"), exist_ok=True)
    with open(os.path.join(steam, "steamapps", "libraryfolders.vdf"), "w") as f:
        f.write(
            '"libraryfolders"\n{\n'
            '\t"0"\n\t{\n'
            f'\t\t"path"\t\t"{steam}"\n'
            "\t}\n"
            '\t"1"\n\t{\n'
            f'\t\t"path"\t\t"{steam_library}"\n'
            "\t}\n"
            '\t"2"\n\t{\n'
            f'\t\t"path"\t\t"{unmounted_library}"\n'
            '\t\t"apps"\n\t\t{\n'
            f'\t\t\t"{PHANTOM_INSTALLED_APPID}"\t\t"123456789"\n'
            "\t\t}\n"
            "\t}\n"
            "}\n")

    # --- Heroic ------------------------------------------------------------------------------
    heroic = build_heroic(home, games)

    # --- shortcuts.vdf ------------------------------------------------------------------------
    vdf_dir = os.path.join(steam, "userdata", "1234567", "config")
    os.makedirs(vdf_dir, exist_ok=True)
    entries = (
        shortcut(0, "Fake Prefix Game",
                 os.path.join(prefix_install, "game.exe"), prefix_install, PREFIX_APPID_SIGNED)
        + shortcut(1, "Fake Portable Game",
                   os.path.join(portable_install, "game.exe"), portable_install,
                   PORTABLE_APPID_SIGNED)
        # Heroic's "Add to Steam" entry for game 3. Steam has no compatdata for it — it does not
        # launch it — so this row alone can never produce a save path. It exists to prove the
        # Heroic pass wins the dedupe.
        + shortcut(2, "Heroic Prefix Game",
                   os.path.join(heroic["epic_install"], "game.exe"), heroic["epic_install"],
                   HEROIC_SHORTCUT_APPID_SIGNED)
        # MoonDeck's own entry: Exe is the streaming client, StartDir is MoonDeck's plugin
        # directory (not the game's), and LaunchOptions is where the real local appid lives.
        + shortcut(3, "MoonDeck Streamed Game",
                   os.path.join(moondeck_script_dir, "moondeckrun.sh"), moondeck_script_dir,
                   MOONDECK_SHORTCUT_APPID_SIGNED,
                   launch_options=(
                       "MOONDECK_APP_TYPE=0 "
                       f"MOONDECK_STEAM_APP_ID={MOONDECK_REAL_APPID} "
                       "MOONDECK_AUTO_RES=1280x800 "
                       "MOONDECK_LINKED_DISPLAY='Fake Display (1280x800 mm)' "
                       "%command%"))
        # A stray duplicate of the same name, no LaunchOptions, own appid also dead — the shape
        # found on a real Deck where a launcher re-creates a shortcut instead of updating it.
        + shortcut(4, "MoonDeck Streamed Game",
                   os.path.join(moondeck_script_dir, "moondeckrun.sh"), moondeck_script_dir,
                   MOONDECK_DUP_APPID_SIGNED)
        # MoonDeck shortcut for the game that is ALSO genuinely installed (Cyberpunk 2077 shape).
        + shortcut(5, "Fake Dual Source Game",
                   os.path.join(moondeck_script_dir, "moondeckrun.sh"), moondeck_script_dir,
                   DUAL_SOURCE_MOONDECK_SHORTCUT_APPID_SIGNED,
                   launch_options=f"MOONDECK_APP_TYPE=0 MOONDECK_STEAM_APP_ID={DUAL_SOURCE_APPID} %command%")
        # MoonDeck shortcut for a game installed on a library that is not mounted AT ALL — no ACF
        # reachable anywhere, only Steam's own "apps" bookkeeping. The exact Cyberpunk 2077 shape.
        + shortcut(6, "Fake Phantom Installed Game",
                   os.path.join(moondeck_script_dir, "moondeckrun.sh"), moondeck_script_dir,
                   PHANTOM_INSTALLED_MOONDECK_SHORTCUT_APPID_SIGNED,
                   launch_options=(
                       "MOONDECK_APP_TYPE=0 "
                       f"MOONDECK_STEAM_APP_ID={PHANTOM_INSTALLED_APPID} %command%"))
    )
    with open(os.path.join(vdf_dir, "shortcuts.vdf"), "wb") as f:
        f.write(build_shortcuts_vdf(entries))

    # Report the paths the harness asserts against. Deliberately HOME_DIR, not HOME: the caller
    # eval's this, and clobbering $HOME mid-script would be a nasty surprise.
    print(f"HOME_DIR={home}")
    print(f"STEAM_ROOT={steam}")
    print(f"PREFIX={prefix}")
    print(f"PREFIX_APPID={PREFIX_APPID_UNSIGNED}")
    print(f"PREFIX_SAVE={in_prefix_save}")
    print(f"PORTABLE_APPID={PORTABLE_APPID_UNSIGNED}")
    print(f"PORTABLE_PREFIX={os.path.join(steam, 'steamapps', 'compatdata', PORTABLE_APPID_UNSIGNED)}")
    print(f"PORTABLE_SAVE={portable_save}")
    print(f"PORTABLE_INSTALL={portable_install}")
    print(f"INSTALLED_APPID={INSTALLED_APPID}")
    print(f"INSTALLED_LIBRARY={steam_library}")
    print(f"INSTALLED_PREFIX={installed_prefix}")
    print(f"INSTALLED_SAVE={installed_save}")
    print(f"CLOUD_SAVE={cloud_saves[CLOUD_APPID]}")
    print(f"GOG_CLOUD_SAVE={cloud_saves[GOG_CLOUD_APPID]}")
    print(f"RELOCATED_APPID={RELOCATED_APPID}")
    print(f"RELOCATED_SAVE={relocated_save}")
    print(f"CLOUD_ONLY_APPID={CLOUD_ONLY_APPID}")
    print(f"CLOUD_ONLY_SAVE={cloud_only_save}")
    print(f"DUAL_SOURCE_APPID={DUAL_SOURCE_APPID}")
    print(f"DUAL_SOURCE_SAVE={dual_source_save}")
    print(f"UNMOUNTED_LIBRARY={unmounted_library}")
    print(f"PHANTOM_INSTALLED_APPID={PHANTOM_INSTALLED_APPID}")
    print(f"PHANTOM_INSTALLED_SAVE={phantom_installed_save}")
    # shlex.quote, unlike every other path printed above: those are all deliberately built with no
    # spaces so the bare KEY=value form survives eval unquoted, but "My games"/"My Games" is the
    # exact real-world folder name under test and cannot be spelled without one.
    print(f"MISCASED_PREFIX={shlex.quote(miscased_prefix)}")
    print(f"MISCASED_SAVE={shlex.quote(miscased_save)}")
    print(f"TIEBREAK_PREFIX={shlex.quote(tiebreak_prefix)}")
    print(f"TIEBREAK_OLDER_SAVE={shlex.quote(tiebreak_older_save)}")
    print(f"TIEBREAK_NEWER_SAVE={shlex.quote(tiebreak_newer_save)}")
    print(f"HEROIC_CONFIG={heroic['config_root']}")
    print(f"HEROIC_EPIC_PREFIX={heroic['epic_prefix']}")
    print(f"HEROIC_EPIC_SAVE={heroic['epic_save']}")
    print(f"HEROIC_WINE_PREFIX={heroic['wine_prefix']}")
    print(f"HEROIC_WINE_SAVE={heroic['wine_save']}")
    print(f"HEROIC_LINUX_INSTALL={heroic['linux_install']}")
    print(f"HEROIC_RENAMED_PREFIX={heroic['renamed_prefix']}")
    print(f"HEROIC_CONFIGURED_PREFIX={heroic['configured_prefix']}")
    print(f"HEROIC_STRANDED_SAVE={heroic['stranded_save']}")
    print(f"HEROIC_SIDELOAD_LIBRARY={heroic['sideload_library']}")
    print(f"MOONDECK_REAL_APPID={MOONDECK_REAL_APPID}")
    print(f"MOONDECK_REAL_PREFIX={moondeck_prefix}")
    print(f"MOONDECK_REAL_SAVE={moondeck_save}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
