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
"""
import os
import struct
import sys

PREFIX_APPID_SIGNED = -1234567890          # what shortcuts.vdf stores
PREFIX_APPID_UNSIGNED = str(PREFIX_APPID_SIGNED & 0xFFFFFFFF)  # 3060399406 — the folder name
PORTABLE_APPID_SIGNED = 1234567890
PORTABLE_APPID_UNSIGNED = str(PORTABLE_APPID_SIGNED & 0xFFFFFFFF)


def cstr(s: str) -> bytes:
    return s.encode("utf-8") + b"\x00"


def vdf_string(key: str, value: str) -> bytes:
    return b"\x01" + cstr(key) + cstr(value)


def vdf_int(key: str, value: int) -> bytes:
    return b"\x02" + cstr(key) + struct.pack("<i", value)


def shortcut(index: int, name: str, exe: str, start_dir: str, appid: int) -> bytes:
    body = (
        vdf_int("appid", appid)
        + vdf_string("AppName", name)
        + vdf_string("Exe", f'"{exe}"')          # Steam quotes these
        + vdf_string("StartDir", f'"{start_dir}"')
    )
    return b"\x00" + cstr(str(index)) + body + b"\x08"


def build_shortcuts_vdf(entries: bytes) -> bytes:
    # Root object: 0x00 "shortcuts" <children> 0x08, plus Steam's trailing 0x08.
    return b"\x00" + cstr("shortcuts") + entries + b"\x08" + b"\x08"


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

    # --- shortcuts.vdf ------------------------------------------------------------------------
    vdf_dir = os.path.join(steam, "userdata", "1234567", "config")
    os.makedirs(vdf_dir, exist_ok=True)
    entries = (
        shortcut(0, "Fake Prefix Game",
                 os.path.join(prefix_install, "game.exe"), prefix_install, PREFIX_APPID_SIGNED)
        + shortcut(1, "Fake Portable Game",
                   os.path.join(portable_install, "game.exe"), portable_install,
                   PORTABLE_APPID_SIGNED)
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
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
