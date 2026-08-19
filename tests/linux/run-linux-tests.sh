#!/usr/bin/env bash
# Linux agent integration tests — the fake-game harness.
#
# Covers the whole Phase 2 path with no Steam, no Proton, no GPU and no Steam Deck:
#   * shortcuts.vdf discovery, including the signed -> unsigned AppID trap
#   * Proton prefix resolution (manifest tokens resolving INSIDE the prefix)
#   * both save shapes: in-prefix AND portable-next-to-the-exe
#   * Heroic Games Launcher: its own library files, its own prefixes — proton-shaped AND
#     wine-shaped — and the double-listing when a Heroic game is also added to Steam
#   * the launch wrapper: pull on launch, supervise the child, settle, push on exit
#   * the settle gate waiting out a game that flushes after it exits
#   * the /proc lock probe seeing a held write descriptor
#   * doctor's diagnostics: the right state root under --config, and an unenrolled machine
#     reported as an enrolment problem rather than a network one
#   * autostart reporting systemctl's real exit status
#
# MUST be run from a Linux filesystem (~/), never /mnt/c: DrvFs breaks inotify, permissions,
# case-sensitivity and locking, so a green run there would be a fiction.
#
# Usage: tests/linux/run-linux-tests.sh
set -uo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
scratch="${repo_root}/.verify-linux"
fixtures="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
port=5179
server_url="http://127.0.0.1:${port}"

pass=0
fail=0

check() {
  if [ "$2" = "0" ]; then echo "PASS: $1"; pass=$((pass + 1))
  else echo "FAIL: $1"; fail=$((fail + 1)); fi
}
contains() { grep -qF -- "$2" <<<"$1" && echo 0 || echo 1; }

case "${repo_root}" in
  /mnt/*) echo "REFUSING: ${repo_root} is a Windows drive (DrvFs). Run from the WSL ext4 home."; exit 2 ;;
esac

command -v python3 >/dev/null || { echo "python3 is required to build the fixtures."; exit 2; }

cleanup() {
  [ -n "${server_pid:-}" ] && kill "${server_pid}" 2>/dev/null
  pkill -f "${scratch}" 2>/dev/null   # any straggling slow-game writers
  wait 2>/dev/null
  return 0
}
trap cleanup EXIT

rm -rf "${scratch}"
mkdir -p "${scratch}"

# ── Fixtures ────────────────────────────────────────────────────────────────────────────────
eval "$(python3 "${fixtures}/make-fixtures.py" "${scratch}")"

# Everything below runs against the FAKE home, so the agent's Steam-root probe, its XDG state
# dir and its config all land in the fixture tree rather than the developer's real home.
export HOME="${HOME_DIR}"
export XDG_DATA_HOME="${HOME_DIR}/.local/share"
echo "Fixtures in ${scratch} (fake HOME=${HOME})"

# ── Server (fresh DB) ───────────────────────────────────────────────────────────────────────
echo "==> Starting server on ${server_url}"
server_state="${scratch}/server"
mkdir -p "${server_state}/archives"
# AgentInstallerRoot matters as much as the other two, and only on Linux. AgentInstallerService
# creates its root in its CONSTRUCTOR, and the default resolves to /data/agent-installer — which a
# normal user cannot create. The server then died at startup AFTER migrations had run, so the log
# looked like a healthy boot right up to an unhandled UnauthorizedAccessException, and all 16
# server-dependent checks below failed as though the agent were broken. Windows never saw it: there
# the same default happily creates E:\data\.
ASPNETCORE_URLS="${server_url}" \
Storage__DbPath="${server_state}/savelocker.db" \
Storage__ArchiveRoot="${server_state}/archives" \
Storage__AgentInstallerRoot="${server_state}/agent-installer" \
  dotnet run --project "${repo_root}/src/Server/SaveLocker.Server.csproj" \
    --no-launch-profile >"${scratch}/server.log" 2>&1 &
server_pid=$!

# /api/admin/status is the only unauthenticated route. /api/games is an AGENT route and answers
# 401 without an X-Api-Key, so probing it never succeeds and this loop silently burned all 60s.
for _ in $(seq 1 60); do
  curl -sf "${server_url}/api/admin/status" >/dev/null 2>&1 && break
  sleep 1
done

# ── Agent under test ────────────────────────────────────────────────────────────────────────
agent_dir="${repo_root}/src/Agent.Linux"
agent() { dotnet run --project "${agent_dir}/SaveLocker.Agent.Linux.csproj" -v quiet --no-build -- "$@" 2>&1; }

echo "==> Building agent"
dotnet build "${agent_dir}/SaveLocker.Agent.Linux.csproj" -v quiet --nologo >"${scratch}/build.log" 2>&1 \
  || { echo "BUILD FAILED"; tail -30 "${scratch}/build.log"; exit 1; }

deck_cfg="${scratch}/deck-config.json"
other_cfg="${scratch}/other-config.json"
for c in "${deck_cfg}" "${other_cfg}"; do
  cat >"${c}" <<EOF
{
  "ServerUrl": "${server_url}",
  "ManifestCachePath": "${fixtures}/manifest.yaml",
  "SettleQuietSeconds": 3,
  "SettleMaxWaitSeconds": 60,
  "Games": []
}
EOF
done

out="$(agent register --config "${deck_cfg}" --name Deck)"
check "Deck registered" "$(contains "${out}" "Registered 'Deck'")"
out="$(agent register --config "${other_cfg}" --name Desktop)"
check "second machine registered" "$(contains "${out}" "Registered 'Desktop'")"

# ── Discovery: shortcuts.vdf + the signed/unsigned AppID trap ────────────────────────────────
out="$(agent scan --config "${deck_cfg}")"
check "scan finds the non-Steam shortcut"      "$(contains "${out}" "Fake Prefix Game")"
check "scan finds the portable shortcut"       "$(contains "${out}" "Fake Portable Game")"
# The vdf stores -1234567890; compatdata is named 3060399406. Reading the signed form misses.
check "AppID converted signed -> unsigned"     "$(contains "${out}" "appid=${PREFIX_APPID}")"
check "scan resolves the in-prefix save dir"   "$(contains "${out}" "${PREFIX_SAVE}")"
check "scan resolves the portable save dir"    "$(contains "${out}" "${PORTABLE_SAVE}")"
# The portable game's manifest entry saves to a loose file beside its .exe (the Cave Story+ shape),
# so it resolves to the INSTALL DIRECTORY — the whole game. That answer must be dropped: syncing it
# archives the game, and restore's delete pass would prune another machine's installation to match.
# Without the guard the install dir resolves first and beats the real save folder above.
# Whole-line match: the real save dir is ${PORTABLE_INSTALL}/Saves, so a substring test for the
# install directory matches it too and passes no matter what the code does.
check "the install directory is not offered as a save dir" \
  "$(printf '%s\n' "${out}" | grep -qxF "      save: ${PORTABLE_INSTALL}" && echo 1 || echo 0)"
# --prefix is required: without one there is no resolver on Linux at all, so the command would
# report "not found" for a reason that has nothing to do with the guard under test.
out2="$(agent resolve --config "${deck_cfg}" --prefix "${PORTABLE_PREFIX}" \
        --install-dir "${PORTABLE_INSTALL}" "Fake Portable Game")"
check "resolve refuses a <base>-only save path" \
  "$(contains "${out2}" "no existing save directory found")"

# ── Discovery: MoonDeck-streamed shortcuts ────────────────────────────────────────────────────
# A MoonDeck shortcut's Exe launches its streaming client, not the game, so Steam never creates a
# compatdata prefix for the shortcut's OWN AppID. When the same title is also installed locally
# under Proton, the real AppID lives in LaunchOptions (MOONDECK_STEAM_APP_ID=...) and that prefix
# is where the game's actual saves are — found on a real Deck where Cyberpunk 2077 was invisible to
# scan for exactly this reason.
check "scan finds the MoonDeck shortcut"           "$(contains "${out}" "MoonDeck Streamed Game")"
check "scan resolves save via MOONDECK_STEAM_APP_ID" \
  "$(contains "${out}" "${MOONDECK_REAL_SAVE}")"
# The Cyberpunk 2077 shape: a game that is BOTH genuinely installed (real Steam Cloud) AND has a
# MoonDeck shortcut pointing at it. The installed copy must survive the dedupe — a SteamShortcut
# candidate is unconditionally HasSteamCloud: false, so without the MoonDeck-aware tie-break this
# would wrongly favor the streaming pointer purely because "prefer the copy Cloud doesn't cover"
# does not know the pointer isn't an independent build. Regression pin for a bug introduced by the
# MoonDeck fix itself: before that fix the pointer never resolved anything, so it never reached this
# tie by construction — not because the ordering was correct.
check "dual-source game resolves" \
  "$(contains "${out}" "${DUAL_SOURCE_SAVE}")"
check "the genuinely-installed copy wins over its own MoonDeck shortcut" \
  "$(contains "${out}" "Fake Dual Source Game  <SteamInstalled> [Steam Cloud] appid=${DUAL_SOURCE_APPID}")"
check "only one Fake Dual Source Game row survives the dedupe" \
  "$([ "$(printf '%s\n' "${out}" | grep -c 'Fake Dual Source Game')" = 1 ] && echo 0 || echo 1)"

# The Cyberpunk 2077 shape exactly: installed on a library that is not mounted AT ALL (no ACF
# reachable anywhere), known only via Steam's own "apps" bookkeeping and a MoonDeck shortcut naming
# the real AppID. Must be reported as SteamInstalled, not SteamShortcut — a resolved compatdata
# prefix alone could never justify that (Steam does not clean up compatdata on uninstall, so a real
# prefix proves nothing about whether the game is STILL installed), but the apps block does.
check "scan finds the phantom-installed game"       "$(contains "${out}" "${PHANTOM_INSTALLED_SAVE}")"
check "it is reported as SteamInstalled, not a shortcut" \
  "$(contains "${out}" "Fake Phantom Installed Game  <SteamInstalled> [Steam Cloud] appid=${PHANTOM_INSTALLED_APPID}")"
# A duplicate shortcut (same name, different dead AppID, no LaunchOptions) must not win the dedupe
# over the one that actually resolves — the shape found on the real Deck for other titles.
check "the resolving MoonDeck row wins over its dead duplicate" \
  "$(contains "${out}" "MoonDeck Streamed Game  <SteamShortcut>")"
check "only one MoonDeck Streamed Game row survives the dedupe" \
  "$([ "$(printf '%s\n' "${out}" | grep -c 'MoonDeck Streamed Game')" = 1 ] && echo 0 || echo 1)"

# ── Discovery: installed Steam games ────────────────────────────────────────────────────────
# These were deliberately not discovered at all until now, on the reasoning that Steam Cloud covers
# them. The UI filters what the scan returns, so "hidden by default" and "never scanned" look
# identical from the couch — and only one of them can be undone by the user.
check "scan finds the installed Steam game"    "$(contains "${out}" "Fake Installed Game")"
# The library is on a second volume with its own compatdata. Resolving this proves the scan pairs
# each library with its own prefixes rather than looking in the main Steam root.
check "installed game resolves in its own library's prefix" "$(contains "${out}" "${INSTALLED_SAVE}")"
# A runtime is an installed "app" and not a game. Listing it is not cosmetic: it is enrollable.
check "Steam runtimes are not offered as games" \
  "$([ "$(contains "${out}" "Steamworks Common Redistributables")" = 1 ] && echo 0 || echo 1)"
# Its appid is deliberately one no code could have hardcoded. Found on a Deck: the filter WAS a
# list of appids, and Valve mints a new one per Proton release, so Proton 9/10/11, Proton Hotfix
# and Steam Linux Runtime 4.0 all arrived as enrollable games. toolmanifest.vdf is the real marker.
check "an unknown compat tool is not offered as a game" \
  "$([ "$(contains "${out}" "Proton 99.0")" = 1 ] && echo 0 || echo 1)"

# A game relocated to a second library (e.g. moved to an SD card to free space) can end up with a
# fresh, USELESS prefix at the new location (Steam re-creates one; it just has nothing the manifest
# matches) while the ORIGINAL prefix, left behind in the main root, has the real save history —
# found on a real Deck (2026-08-19): Borderlands 2. Resolves only if the scan retries the main root
# whenever the current library's own prefix resolves NOTHING, not only when it is missing outright.
check "scan finds the relocated game"              "$(contains "${out}" "Fake Relocated Game")"
check "relocated game resolves via the main root's prefix" \
  "$(contains "${out}" "${RELOCATED_SAVE}")"

# A game with NO compatdata prefix anywhere — its only save path is Steam's own Cloud sync folder
# under userdata, which needs no Wine prefix at all. Found on the same Deck: Left 4 Dead 2. Also
# proves <root> comes from the real Steam root and not from a library the (missing) prefix path
# happens to point at — userdata exists exactly once, never per-library.
check "scan finds the cloud-only game"              "$(contains "${out}" "Fake Cloud Only Game")"
check "cloud-only game resolves with no prefix at all" \
  "$(contains "${out}" "${CLOUD_ONLY_SAVE}")"

# ── Steam Cloud is per-game manifest data, not "Steam installed it" ──────────────────────────
# The flag was hardcoded true for every installed Steam title, and the default Add Games view HIDES
# whatever is flagged — so a game with no cloud behind it at all was invisible, and the user was
# never told it existed. Most Steam titles are in that position: of the manifest's 48,908 entries
# with a Steam id, only 14,340 have Steam Cloud.
#
# The three games below differ in NOTHING the scanner can observe except their manifest cloud block,
# which is what makes this a test of reading it rather than of any particular constant.
check "no cloud block -> not flagged as Steam Cloud" \
  "$(contains "${out}" "Fake Installed Game  <SteamInstalled> appid=")"
check "cloud: steam: true -> flagged as Steam Cloud" \
  "$(contains "${out}" "Fake Cloud Game  <SteamInstalled> [Steam Cloud] appid=")"
# The Breath of Fire IV shape, and the case that motivated this: a cloud block that exists, is
# correct, and does not name Steam. Sold on Steam; only GOG syncs it.
check "cloud: gog only -> not flagged as Steam Cloud" \
  "$(contains "${out}" "Fake Gog Cloud Game  <SteamInstalled> appid=")"

# The flag is only worth anything if the filter acts on it — this is the view the user actually sees.
out_nocloud="$(agent scan --config "${deck_cfg}" --no-cloud)"
check "--no-cloud keeps a Steam game the manifest does not cloud" \
  "$(contains "${out_nocloud}" "Fake Installed Game")"
check "--no-cloud keeps the gog-cloud-only game" \
  "$(contains "${out_nocloud}" "Fake Gog Cloud Game")"
check "--no-cloud drops the genuinely cloud-synced game" \
  "$([ "$(contains "${out_nocloud}" "Fake Cloud Game  <")" = 1 ] && echo 0 || echo 1)"

# ── Prefix resolution: manifest tokens must resolve INSIDE the prefix ────────────────────────
out="$(agent resolve --config "${deck_cfg}" --prefix "${PREFIX}" "Fake Prefix Game")"
check "manifest <winAppData> resolves inside the prefix" "$(contains "${out}" "${PREFIX_SAVE}")"

# The same lookup WITHOUT a prefix must not invent a host path.
out="$(agent resolve --config "${deck_cfg}" "Fake Prefix Game")"
check "no prefix -> no resolution (does not guess a host path)" \
  "$(contains "${out}" "no existing save directory found")"

# ── Heroic Games Launcher ───────────────────────────────────────────────────────────────────
# Heroic owns its own prefixes and obeys neither Steam convention, so nothing here is covered by
# the shortcut checks above.
out="$(agent scan --config "${deck_cfg}")"
check "scan finds the Heroic Epic game"        "$(contains "${out}" "Heroic Prefix Game")"
check "scan finds the Heroic sideloaded game"  "$(contains "${out}" "Heroic Wine Game")"
# Proton-shaped prefix (pfx/, steamuser) that Steam did not create.
check "Heroic proton-shaped prefix resolves"   "$(contains "${out}" "${HEROIC_EPIC_SAVE}")"
# Wine-shaped prefix (drive_c/ directly, Linux username). Unresolvable by anything assuming Steam.
check "Heroic wine-shaped prefix resolves"     "$(contains "${out}" "${HEROIC_WINE_SAVE}")"
# Native-Linux titles are out of scope (Decisions.md §1) — they have no prefix to resolve in.
check "native-Linux Heroic game not listed" \
  "$([ "$(contains "${out}" "Heroic Linux Game")" = 1 ] && echo 0 || echo 1)"

# The Epic game ALSO has a shortcuts.vdf entry from Heroic's "Add to Steam", with no compatdata
# behind it. One game, one row — and it must be the row that has a save dir, not the empty one.
check "Heroic + Steam shortcut collapse to one row" \
  "$([ "$(printf '%s\n' "${out}" | grep -c 'Heroic Prefix Game')" = 1 ] && echo 0 || echo 1)"
check "the surviving row is the Heroic one"    "$(contains "${out}" "Heroic Prefix Game  <Heroic>")"

# --prefix must read the prefix's real layout rather than assume Steam's.
out="$(agent resolve --config "${deck_cfg}" --prefix "${HEROIC_WINE_PREFIX}" "Heroic Wine Game")"
check "resolve --prefix handles a wine-shaped prefix" "$(contains "${out}" "${HEROIC_WINE_SAVE}")"

# ── A sideloaded game whose files and configured prefix are two different prefixes ────────────
# Reproduced on a Deck (2026-08-09) on a CLEAN install, no renaming involved: Heroic's sideload
# dialog seeds the prefix name from a title field that starts as the literal word "Title", so the
# game installs into one prefix and is configured to launch in another. Renaming a prefix by hand
# produces the same split.
#
# Either way the game runs in the CONFIGURED prefix, so that is where saves go. The other prefix can
# hold a full set of real save files that nothing writes to any more — the more convincing of the
# two answers, and the wrong one. Returning nothing beats returning it.
cp "${HEROIC_SIDELOAD_LIBRARY%.json}.renamed-prefix.json" "${HEROIC_SIDELOAD_LIBRARY}"

out="$(agent scan --config "${deck_cfg}")"
check "split prefix: the game is still discovered" "$(contains "${out}" "Heroic Offline Game")"
check "split prefix: no save path is guessed" \
  "$([ "$(contains "${out}" "${HEROIC_STRANDED_SAVE}")" = 1 ] && echo 0 || echo 1)"
# Proof the case is live: the stranded path DOES resolve if you point the resolver at that prefix.
out="$(agent resolve --config "${deck_cfg}" --prefix "${HEROIC_RENAMED_PREFIX}" "Heroic Offline Game")"
check "split prefix: the stranded save would have resolved" \
  "$(contains "${out}" "${HEROIC_STRANDED_SAVE}")"

out="$(agent doctor --config "${deck_cfg}")"
doctor_split_rc=$?
check "split prefix: doctor reports the disagreement" \
  "$(contains "${out}" "its executable is in")"
check "split prefix: doctor names both prefixes" \
  "$([ "$(contains "${out}" "${HEROIC_RENAMED_PREFIX}")" = 0 ] && \
     [ "$(contains "${out}" "${HEROIC_CONFIGURED_PREFIX}")" = 0 ] && echo 0 || echo 1)"
# A NOTE, not a problem. Heroic splits these by default for sideloaded games, so failing doctor's
# exit code over it would report a working Deck as broken.
check "split prefix: doctor still exits 0" "${doctor_split_rc}"

# Back to the healthy library, so the checks below still describe a clean machine.
python3 - "${HEROIC_SIDELOAD_LIBRARY}" <<'PY'
import json, sys
p = sys.argv[1]
lib = json.load(open(p))
lib["games"] = [g for g in lib["games"] if g["app_name"] != "HeroicOfflineGame"]
json.dump(lib, open(p, "w"), indent=2)
PY

# ── Map both games ──────────────────────────────────────────────────────────────────────────
out="$(agent add-game --config "${deck_cfg}" --name "Fake Prefix Game" \
        --manifest "Fake Prefix Game" --prefix "${PREFIX}" --appid "${PREFIX_APPID}")"
check "in-prefix game auto-mapped from the manifest" "$(contains "${out}" "${PREFIX_SAVE}")"

out="$(agent add-game --config "${deck_cfg}" --name "Fake Portable Game" \
        --dir "${PORTABLE_SAVE}" --appid "${PORTABLE_APPID}")"
check "portable game mapped with --dir" "$(contains "${out}" "${PORTABLE_SAVE}")"

# ── doctor ──────────────────────────────────────────────────────────────────────────────────
out="$(agent doctor --config "${deck_cfg}")"
doctor_rc=$?
check "doctor reports no problems"        "${doctor_rc}"
check "doctor finds the Steam root"       "$(contains "${out}" "${STEAM_ROOT}")"
check "doctor resolves the Proton prefix" "$(contains "${out}" "${PREFIX}")"
check "doctor confirms the /proc lock probe" "$(contains "${out}" "available (/proc)")"
check "doctor finds the Heroic config root" "$(contains "${out}" "${HEROIC_CONFIG}")"
# Which shape a prefix has decides where the manifest's tokens resolve, so doctor must say.
check "doctor names the wine-shaped prefix"  "$(contains "${out}" "(wine, user deck)")"
check "doctor names the proton-shaped prefix" "$(contains "${out}" "(proton, user steamuser)")"

# A MoonDeck shortcut's own AppID never gets a prefix, and doctor must not stop at that — it has to
# say the real local prefix its LaunchOptions names DOES exist, since that is what makes the "no
# prefix, launch it once" advice above wrong for this specific shortcut.
check "doctor points at the real local prefix MoonDeck streams" \
  "$(contains "${out}" "MoonDeck streams appid ${MOONDECK_REAL_APPID}")"
check "doctor names the actual prefix path" "$(contains "${out}" "${MOONDECK_REAL_PREFIX}")"
# The duplicate shortcut (same name, different dead AppID) is worth a note, but the machine still
# works — scan's dedupe already picked the one that resolves — so it must not fail doctor's exit code.
check "doctor notes the duplicate MoonDeck shortcut" \
  "$(contains "${out}" "'MoonDeck Streamed Game' has 2 shortcuts with different AppIDs")"

# A library libraryfolders.vdf still names but that does not exist right now (an SD card that isn't
# inserted) must be called out by name, not silently skipped the way LibraryPaths correctly skips it
# for scanning purposes — found on a real Deck (2026-08-19): a genuinely Steam-installed game
# (Cyberpunk 2077, on a card labelled "SD2") had no way to explain why it was invisible.
check "doctor names the unmounted library" \
  "$(contains "${out}" "library configured but not mounted right now: ${UNMOUNTED_LIBRARY}")"
# Not a fault — an unmounted card is completely normal — so it must not fail doctor's exit code.
check "an unmounted library is not a doctor problem" "${doctor_rc}"

# doctor must describe the state root it is ACTUALLY using. ${deck_cfg} is deliberately outside
# AgentConfig.DefaultDir, and doctor used to print (and probe) the default regardless - declaring
# the wrong directory healthy while the one in use might be missing or read-only.
state_dir_line="$(grep -F '  state dir: ' <<<"${out}")"
check "doctor reports the --config state dir" "$(contains "${state_dir_line}" "${scratch}")"
check "doctor does not report the default state dir instead"   "$(grep -qF "${HOME}/.local/share/SaveLocker" <<<"${state_dir_line}" && echo 1 || echo 0)"

# An unenrolled agent gets a correct 401 from an authenticated route. Calling it with no key and
# then labelling the refusal "server unreachable" sent people to debug a network that was fine.
unenrolled_cfg="${scratch}/unenrolled-config.json"
python3 - "${unenrolled_cfg}" "${server_url}" <<'PY2'
import json,sys
json.dump({"ServerUrl": sys.argv[2], "MachineName": "Unenrolled", "Games": []}, open(sys.argv[1], "w"))
PY2
out="$(agent doctor --config "${unenrolled_cfg}")"
check "doctor says an unenrolled machine is NOT a connectivity failure"   "$(grep -q 'server unreachable' <<<"${out}" && echo 1 || echo 0)"
check "doctor names enrolment as the actual problem"   "$(contains "${out}" "not enrolled")"
check "doctor still reports the server as reachable"   "$(contains "${out}" "server reachable")"

# ── The launch wrapper: pull, run the game, settle, push ─────────────────────────────────────
# Seed a save so there is something to push, and prove the wrapper returns the GAME's exit code.
echo "level=0" >"${PREFIX_SAVE}/slot1.sav"

echo "==> Launch wrapper (in-prefix game; writer keeps flushing for 6s after exit)"
start=$(date +%s)
# These MUST be exported, not prefixed onto the assignment: `VAR=x out=$(cmd)` is two plain
# assignments in bash and never puts VAR in the command's environment — which is exactly the
# input this test is about.
export STEAM_COMPAT_DATA_PATH="${PREFIX}"
export SteamAppId="${PREFIX_APPID}"
out="$(agent run --config "${deck_cfg}" -- "${fixtures}/slow-game.sh" "${PREFIX_SAVE}" 6 7)"
run_rc=$?
elapsed=$(( $(date +%s) - start ))
unset STEAM_COMPAT_DATA_PATH SteamAppId

check "wrapper propagates the game's exit code (7)" "$([ "${run_rc}" = "7" ] && echo 0 || echo 1)"

log="${XDG_DATA_HOME}/SaveLocker/agent.log"
logtail="$(tail -60 "${log}" 2>/dev/null)"
check "wrapper saw the prefix from STEAM_COMPAT_DATA_PATH" "$(contains "${logtail}" "prefix=${PREFIX}")"
# The gate must have waited out the post-exit writer rather than archiving immediately.
check "settle gate waited for the save to go quiet" "$(contains "${logtail}" "save files settled.")"
check "settle gate actually took time (>=6s)" "$([ "${elapsed}" -ge 6 ] && echo 0 || echo 1)"
check "wrapper pushed a new version on exit" "$(contains "${logtail}" "pushed new version")"

# The settled save is the COMPLETE one — the writer's last line, not a half-written file.
check "pushed save contains the writer's final line" \
  "$(grep -q '^done$' "${PREFIX_SAVE}/slot1.sav" && echo 0 || echo 1)"

# ── The /proc lock probe, isolated ───────────────────────────────────────────────────────────
# The decisive test for the Linux behaviour change. This writer writes ONCE and then holds the
# file open for writing while staying completely still, so the fingerprint half of the gate goes
# quiet almost at once. Only a working /proc/*/fd probe can hold the gate closed for the 8s the
# descriptor stays open.
#
# A probe that silently answers "nothing is locked" (what FileShare does on Linux) settles here in
# ~3s. That is the exact failure the task file said must not pass silently — so it is asserted on.
echo "==> Lock probe in isolation (silent writer, descriptor held open 8s)"
export STEAM_COMPAT_DATA_PATH="${PREFIX}"
export SteamAppId="${PREFIX_APPID}"
start=$(date +%s)
out="$(agent run --config "${deck_cfg}" -- "${fixtures}/slow-game.sh" "${PREFIX_SAVE}" 8 0 hold)"
held_elapsed=$(( $(date +%s) - start ))
unset STEAM_COMPAT_DATA_PATH SteamAppId

check "/proc probe held the gate for a silent-but-open writer (>=8s, not ~3s)" \
  "$([ "${held_elapsed}" -ge 8 ] && echo 0 || echo 1)"
echo "    (settled after ${held_elapsed}s; a broken probe settles in ~3s)"

# ── The save actually round-trips through the server ─────────────────────────────────────────
# A second machine pulls what the Deck pushed and must get byte-identical content.
out="$(agent add-game --config "${other_cfg}" --name "Fake Prefix Game" --dir "${scratch}/pulled")"
out="$(agent pull --config "${other_cfg}" "Fake Prefix Game")"
check "second machine restored the save" \
  "$(diff -r "${PREFIX_SAVE}" "${scratch}/pulled" >/dev/null 2>&1 && echo 0 || echo 1)"

# ── Portable save shape through the same wrapper ─────────────────────────────────────────────
echo "==> Launch wrapper (portable game — saves next to the .exe, no prefix involved)"
export STEAM_COMPAT_DATA_PATH="${PORTABLE_PREFIX}"
export SteamAppId="${PORTABLE_APPID}"
out="$(agent run --config "${deck_cfg}" -- "${fixtures}/slow-game.sh" "${PORTABLE_SAVE}" 4 0)"
run_rc=$?
unset STEAM_COMPAT_DATA_PATH SteamAppId
logtail="$(tail -40 "${log}" 2>/dev/null)"
check "portable game: wrapper exits cleanly" "$([ "${run_rc}" = "0" ] && echo 0 || echo 1)"
check "portable game: pushed without touching the prefix" \
  "$(contains "${logtail}" "[Fake Portable Game] pushed new version")"

out="$(agent status --config "${deck_cfg}")"
check "both games have a head on the server" \
  "$([ "$(grep -c 'head=' <<<"${out}")" = "2" ] && echo 0 || echo 1)"
check "no conflicts" "$(grep -q 'CONFLICT' <<<"${out}" && echo 1 || echo 0)"

# ── Daemon and launch wrapper, concurrently, on the SAME game ────────────────────────────────
# This is the Deck's real configuration and it is the one shape no other harness reproduces:
# autorun keeps the daemon running (watching every mapped save folder) while Steam starts
# `savelocker run` as a second process. Both sync the same game, through the same config.json,
# offline queue and health-event file.
#
# The damage to look for is NOT a crash. It is the daemon writing its stale in-memory config back
# over the parent version the wrapper just recorded: the next push then presents a stale parent and
# the server rejects it as a conflict — one machine, conflicting with itself, with nothing in the
# log to say why. `tests/run-concurrency-tests.ps1` covers the same contention cross-platform with a
# CLI push as the second actor; this covers it with the real launch wrapper.
echo "==> Daemon + launch wrapper on the same game, concurrently"

dotnet "${agent_dir}/bin/Debug/net10.0/savelocker.dll" daemon \
  --config "${deck_cfg}" --port 5189 >"${scratch}/daemon.log" 2>&1 &
daemon_pid=$!
for _ in $(seq 1 40); do
  curl -sf "http://localhost:5189/" >/dev/null 2>&1 && break
  sleep 0.5
done
check "daemon started alongside the wrapper" \
  "$(kill -0 "${daemon_pid}" 2>/dev/null && echo 0 || echo 1)"

# The daemon now holds a config loaded BEFORE anything below happens.
export STEAM_COMPAT_DATA_PATH="${PREFIX}"
export SteamAppId="${PREFIX_APPID}"
# Third arg is the GAME's exit code, which the wrapper propagates verbatim — so it must be 0 here
# for "exits cleanly" to mean anything. (Propagation itself is asserted earlier, with a 7.)
out="$(agent run --config "${deck_cfg}" -- "${fixtures}/slow-game.sh" "${PREFIX_SAVE}" 5 0)"
wrapper_rc=$?
unset STEAM_COMPAT_DATA_PATH SteamAppId
check "wrapper still exits cleanly with a daemon running" \
  "$([ "${wrapper_rc}" = "0" ] && echo 0 || echo 1)"

# Let the daemon's watcher react to the same writes and persist its own view.
sleep 12

version_after_wrapper="$(python3 - "${deck_cfg}" <<'PY'
import json,sys
cfg=json.load(open(sys.argv[1]))
g=[x for x in cfg["Games"] if x["Name"]=="Fake Prefix Game"]
print(g[0].get("LastKnownVersionId") or "" if g else "")
PY
)"
check "the game still has a parent version after both processes wrote" \
  "$([ -n "${version_after_wrapper}" ] && echo 0 || echo 1)"

check "config.json is still valid JSON" \
  "$(python3 -c 'import json,sys; json.load(open(sys.argv[1]))' "${deck_cfg}" >/dev/null 2>&1 && echo 0 || echo 1)"

# The payoff: a lost parent version shows up HERE, as a conflict against the machine's own push.
out="$(agent push --config "${deck_cfg}" "Fake Prefix Game")"
check "a later push does not conflict with itself" \
  "$(grep -q 'CONFLICT' <<<"${out}" && echo 1 || echo 0)"

# Temp archives must be per-process, or one push deletes the other's mid-upload.
# State lives beside the config file it belongs to, so with --config that is ${scratch}, not the
# machine default under XDG_DATA_HOME.
leftover_tmp="$(find "${scratch}/tmp" -name '*.zip' 2>/dev/null | wc -l)"
check "no temp archives leaked" "$([ "${leftover_tmp}" = "0" ] && echo 0 || echo 1)"

kill "${daemon_pid}" 2>/dev/null
wait "${daemon_pid}" 2>/dev/null

# ---------------------------------------------------------------------------
# autostart must report the REAL outcome (LA-08)
# ---------------------------------------------------------------------------
# `disable` used to throw away systemctl's exit code and print "Auto-start disabled." no matter
# ── The update channel: the agent asks for the LINUX package ─────────────────────────────────
# A Deck asking for updates and being handed a Windows .exe is the whole failure mode this block
# exists for, and it has two shapes: a server that hosts both and must serve the right one, and a
# server too old to know about platforms at all, which answers with the .exe no matter what.
#
# Nothing here is ever executed. `check-update --download` fetches and VERIFIES, and stops.
echo
echo "==> Update channel"
inst_root="${server_state}/agent-installer"

# The tarball is a real gzip stream; content does not matter, only its first two bytes and its
# digest, which the server computes as it stores the file.
printf '\037\213\010fake linux agent tarball' >"${scratch}/savelocker-9.9.9-linux-x64.tar.gz"
curl -sf -X POST "${server_url}/api/admin/agent-installer?version=9.9.9&platform=linux-x64" \
  -F "file=@${scratch}/savelocker-9.9.9-linux-x64.tar.gz" >/dev/null
# A Windows installer in the Windows slot, at a DIFFERENT version. If the agent ignored the
# platform it would report 8.8.8 here, which is why the two versions must not match.
printf 'MZfake windows installer' >"${scratch}/SaveLocker-Agent-Setup-8.8.8.exe"
curl -sf -X POST "${server_url}/api/admin/agent-installer?version=8.8.8" \
  -F "file=@${scratch}/SaveLocker-Agent-Setup-8.8.8.exe" >/dev/null

out="$(agent check-update --config "${deck_cfg}")"
check "check-update sees the Linux package"        "$(contains "${out}" "v9.9.9 is available")"
check "check-update did NOT take the Windows one"  "$(grep -q '8\.8\.8' <<<"${out}" && echo 1 || echo 0)"
check "the offered URL names the Linux platform"   "$(contains "${out}" "platform=linux-x64")"

out="$(agent check-update --download --config "${deck_cfg}")"
check "a good tarball downloads and VERIFIES"      "$(contains "${out}" "VERIFIED")"

# The digest is the only integrity control on plain http (Decisions.md), so it has to be the thing
# that refuses. Rewriting the stored file behind the server's back leaves installer-info.json
# describing bytes that are no longer there — exactly what a substituted download looks like.
printf '\037\213\010tampered payload, same name' >"${inst_root}/linux-x64/savelocker-9.9.9-linux-x64.tar.gz"
out="$(agent check-update --download --config "${deck_cfg}")"
check "a tampered tarball is REFUSED"              "$(contains "${out}" "REFUSED")"
check "the refusal names the checksum"             "$(contains "${out}" "checksum")"

# The payload check, which is the ONLY thing standing between a Deck and a Windows .exe here: the
# digest matches (the server hashed what it was given), the extension matches, and the bytes are
# still an MZ executable. This is what a server that predates platform slots serves to everyone.
printf 'MZ this is a windows executable, not a tarball' >"${scratch}/wrong-payload.tar.gz"
curl -sf -X POST "${server_url}/api/admin/agent-installer?version=9.9.9&platform=linux-x64" \
  -F "file=@${scratch}/wrong-payload.tar.gz;filename=savelocker-9.9.9-linux-x64.tar.gz" >/dev/null
out="$(agent check-update --download --config "${deck_cfg}")"
check "a Windows executable offered to Linux is REFUSED" "$(contains "${out}" "REFUSED")"
check "the refusal says it is not a gzip archive"        "$(contains "${out}" "gzip archive")"

# doctor is the only diagnostic surface a Deck has, so the update state has to reach it.
out="$(agent doctor --config "${deck_cfg}")"
check "doctor reports the available update"        "$(contains "${out}" "update: v9.9.9 available")"

# The daemon's own view, which is what the agent UI reads. This is the path that was hardcoded to
# null on Linux — every other check here goes through the one-shot CLI, which builds its own checker
# and would keep passing while the daemon reported nothing at all.
dotnet "${agent_dir}/bin/Debug/net10.0/savelocker.dll" daemon \
  --config "${deck_cfg}" --port 5188 >"${scratch}/update-daemon.log" 2>&1 &
upd_pid=$!
for _ in $(seq 1 40); do
  curl -sf "http://localhost:5188/" >/dev/null 2>&1 && break
  sleep 0.5
done
# The local API is token-gated (Decisions.md: reaching it is equivalent to owning the box), and the
# token lives beside the config it belongs to. Without the header every request is a 401 — which is
# indistinguishable from "the daemon has no update to report" if you only look at the assertion.
upd_token="$(cat "${scratch}/api-token")"
# The first check is on a short timer rather than inline with startup, so give it room to land.
upd_json=""
for _ in $(seq 1 20); do
  upd_json="$(curl -sf -H "X-SaveLocker-Token: ${upd_token}" \
    "http://localhost:5188/api/agent-version" 2>/dev/null)"
  grep -q '"updateAvailable":true' <<<"${upd_json}" && break
  sleep 1
done
kill "${upd_pid}" 2>/dev/null; wait "${upd_pid}" 2>/dev/null
check "the daemon reports the update to its own API"  "$(contains "${upd_json}" '"updateAvailable":true')"
check "the daemon names the Linux version"            "$(contains "${upd_json}" '"latestVersion":"9.9.9"')"
# The distinction the "install now" button is built on. What this server is offering right now is
# the MZ payload from the check above — refused at stage time — so this daemon is in the state
# "something newer exists" and NOT in the state "it is on this disk, verified". Only the second may
# be offered as an instant install, and an implementation that filled stagedVersion from the update
# result rather than from the marker on disk fails exactly here.
check "available is not staged"                       "$(contains "${upd_json}" '"stagedVersion":null')"

# ── Stage and apply: the agent replaces its own files ────────────────────────────────────────
# Driven against a COPY of the built agent, never bin/Debug itself, and with the config living
# INSIDE that copy — because on Linux the install prefix and the state directory are the same
# place, and "config.json survives an update" is only a real assertion when it is genuinely sitting
# in the tree being replaced.
#
# The tarball's savelocker is a small script rather than a published binary: the updater only ever
# asks it to start and report a version, a self-contained publish takes minutes, and everything
# under test here is about which files move where. The daemon is driven through savelocker.dll,
# which the tarball does not carry, so the real agent code keeps running throughout.
echo
echo "==> Update: stage and apply"
fake_prefix="${scratch}/prefix"
cp -r "${agent_dir}/bin/Debug/net10.0" "${fake_prefix}"
prefix_cfg="${fake_prefix}/config.json"
cat >"${prefix_cfg}" <<EOF
{
  "ServerUrl": "${server_url}",
  "ManifestCachePath": "${fixtures}/manifest.yaml",
  "Games": []
}
EOF
prefix_agent() { dotnet "${fake_prefix}/savelocker.dll" "$@" --config "${prefix_cfg}" 2>&1; }
prefix_agent register --name "UpdPrefix" >/dev/null
orig_sum="$(md5sum "${fake_prefix}/savelocker" | cut -d' ' -f1)"
orig_key="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["ApiKey"])' "${prefix_cfg}")"

# Builds a linux tarball whose savelocker prints $2 for `version` and exits $3 for everything else.
make_agent_tarball() {   # $1 out, $2 version, $3 version-exit-code
  local out="$1" ver="$2" rc="${3:-0}"
  rm -rf "${scratch}/newver"; mkdir -p "${scratch}/newver/SaveLocker"
  cat >"${scratch}/newver/SaveLocker/savelocker" <<EOS
#!/usr/bin/env bash
if [ "\$1" = "version" ]; then echo "${ver}"; exit ${rc}; fi
exit 0
EOS
  chmod +x "${scratch}/newver/SaveLocker/savelocker"
  echo "payload ${ver}" >"${scratch}/newver/SaveLocker/newfile.txt"
  tar -czf "${out}" -C "${scratch}/newver" SaveLocker
}
host_tarball() {   # $1 file, $2 version
  curl -sf -X POST "${server_url}/api/admin/agent-installer?version=$2&platform=linux-x64" \
    -F "file=@$1;filename=savelocker-$2-linux-x64.tar.gz" >/dev/null
}

# ---- A package that writes outside the staging directory is refused ----
# It becomes the code this machine runs, so it is treated as hostile input in the same way a pulled
# save archive is. Nothing may be left behind, and the working agent must be untouched.
python3 - "${scratch}/escape.tar.gz" <<'PYT'
import io, sys, tarfile
t = tarfile.open(sys.argv[1], "w:gz")
data = b"escaped"
info = tarfile.TarInfo("SaveLocker/../../../escaped.txt"); info.size = len(data)
t.addfile(info, io.BytesIO(data))
t.close()
PYT
host_tarball "${scratch}/escape.tar.gz" "9.9.9"
out="$(prefix_agent update)"
check "a package escaping the staging dir is REFUSED"  "$(contains "${out}" "outside the staging directory")"
check "the escaping package installed nothing"         "$([ "$(md5sum "${fake_prefix}/savelocker" | cut -d' ' -f1)" = "${orig_sum}" ] && echo 0 || echo 1)"
# Where that entry actually resolves to: staged/SaveLocker/../../../escaped.txt is the prefix root.
check "the escaping package wrote nothing outside"     "$([ ! -f "${fake_prefix}/escaped.txt" ] && [ ! -f "${scratch}/escaped.txt" ] && echo 0 || echo 1)"

# ---- A package containing a link is refused outright ----
# Stronger than resolving links and checking where they land: the published tarball is a plain
# `dotnet publish` tree and contains none, so a link in one is either a corrupt build or an attempt
# to write through it. A symlink to /etc is the shape that matters — extract it, then write "into"
# it, and the write lands outside the staging directory without any path ever looking wrong.
python3 - "${scratch}/symlink.tar.gz" <<'PYT'
import sys, tarfile
t = tarfile.open(sys.argv[1], "w:gz")
info = tarfile.TarInfo("SaveLocker/escape")
info.type = tarfile.SYMTYPE
info.linkname = "/etc"
t.addfile(info)
t.close()
PYT
host_tarball "${scratch}/symlink.tar.gz" "9.9.9"
out="$(prefix_agent update)"
check "a package containing a link is REFUSED"         "$(contains "${out}" "contains a link")"
check "the link package installed nothing"             "$([ "$(md5sum "${fake_prefix}/savelocker" | cut -d' ' -f1)" = "${orig_sum}" ] && echo 0 || echo 1)"
check "no link was created in the staging tree"        "$([ ! -L "${fake_prefix}/update/staged/SaveLocker/escape" ] && echo 0 || echo 1)"

# ---- A package that is not the version it claims is refused ----
# The digest proves the bytes came from the server; it says nothing about what they contain. Only
# running the thing does.
make_agent_tarball "${scratch}/wrongver.tar.gz" "1.2.3"
host_tarball "${scratch}/wrongver.tar.gz" "9.9.9"
out="$(prefix_agent update)"
check "a package reporting the wrong version is REFUSED" "$(contains "${out}" "not what it claimed")"
check "the wrong-version package installed nothing"      "$([ "$(md5sum "${fake_prefix}/savelocker" | cut -d' ' -f1)" = "${orig_sum}" ] && echo 0 || echo 1)"

# ---- A package that cannot run is refused ----
# This is the one that would brick a headless Deck: it unpacks perfectly, hashes correctly, and
# only fails when something tries to execute it — by which time the working agent is gone.
make_agent_tarball "${scratch}/broken.tar.gz" "9.9.9" 1
host_tarball "${scratch}/broken.tar.gz" "9.9.9"
out="$(prefix_agent update)"
check "a package that cannot run is REFUSED"           "$(contains "${out}" "Refusing to install it")"
check "the unrunnable package installed nothing"       "$([ "$(md5sum "${fake_prefix}/savelocker" | cut -d' ' -f1)" = "${orig_sum}" ] && echo 0 || echo 1)"

# ---- The good one: the daemon stages it, and does NOT apply it ----
make_agent_tarball "${scratch}/good.tar.gz" "9.9.9"
host_tarball "${scratch}/good.tar.gz" "9.9.9"
dotnet "${fake_prefix}/savelocker.dll" daemon --config "${prefix_cfg}" --port 5187 \
  >"${scratch}/stage-daemon.log" 2>&1 &
stage_pid=$!
for _ in $(seq 1 40); do
  [ -f "${fake_prefix}/update/apply.json" ] && break
  sleep 0.5
done
check "the daemon staged the update"                   "$([ -f "${fake_prefix}/update/apply.json" ] && echo 0 || echo 1)"
check "the daemon did NOT swap the running agent"      "$([ "$(md5sum "${fake_prefix}/savelocker" | cut -d' ' -f1)" = "${orig_sum}" ] && echo 0 || echo 1)"
# Read while this daemon is still up, and deterministic rather than timed: apply.json exists, so the
# answer cannot be "not yet". This field is what the Decky plugin's "Install update now" button
# hangs off, and nothing may offer that button on `updateAvailable` alone.
stage_json="$(curl -sf -H "X-SaveLocker-Token: $(cat "${fake_prefix}/api-token")" \
  "http://localhost:5187/api/agent-version" 2>/dev/null)"
kill "${stage_pid}" 2>/dev/null; wait "${stage_pid}" 2>/dev/null
check "the daemon publishes the STAGED version"        "$(contains "${stage_json}" '"stagedVersion":"9.9.9"')"
check "with no game running, nothing blocks it"        "$(contains "${stage_json}" '"stagedBlockedReason":null')"

# ---- A staged update waits while a game is running ----
# The wrapper is a REAL `savelocker run`, because that is what the /proc scan looks for: argv[0]
# basename savelocker, argv[1] run. A shell script would not do — the kernel puts the interpreter
# in argv[0], so the scan would never see it and the check would pass for the wrong reason.
#
# DOTNET_ROOT is needed only here, and only in a dev tree: this is the framework-dependent apphost,
# which looks for a runtime in /usr/share/dotnet and does not know about a per-user install. A
# released agent is published self-contained and has no such problem — but without this the wrapper
# never starts, the /proc scan correctly finds no game, and the deferral check passes nothing while
# looking exactly like a working feature.
#
# Derived from the dotnet on PATH, NOT from $HOME: this suite reassigns HOME to the fixture tree, so
# "$HOME/.dotnet" resolves inside the fake home and finds nothing.
dotnet_root="$(dirname "$(readlink -f "$(command -v dotnet)")")"
DOTNET_ROOT="${dotnet_root}" \
  "${fake_prefix}/savelocker" run --config "${prefix_cfg}" -- sleep 60 >"${scratch}/wrapper.log" 2>&1 &
wrapper_pid=$!
sleep 3
out="$(prefix_agent apply-update)"
check "apply defers while a game is running"           "$(contains "${out}" "is running")"
check "the deferred apply changed nothing"             "$([ "$(md5sum "${fake_prefix}/savelocker" | cut -d' ' -f1)" = "${orig_sum}" ] && echo 0 || echo 1)"
check "the update is still staged for next time"       "$([ -f "${fake_prefix}/update/apply.json" ] && echo 0 || echo 1)"

# The deferral is SAFE and INVISIBLE, which is the worst pair of properties a button can have: press
# restart with a game open and everything succeeds while nothing changes. So the condition has to be
# published before anyone offers the button, in words the surface can print without rephrasing.
out="$(prefix_agent doctor)"
check "doctor reports the staged version"              "$(contains "${out}" "staged: v9.9.9")"
check "doctor says the running game is why"            "$(contains "${out}" "is running — the update will install when you close it.")"
kill "${wrapper_pid}" 2>/dev/null; wait "${wrapper_pid}" 2>/dev/null

# ---- With no game running, doctor answers "how do I take it now?" ----
# The question this whole feature came from. "It installs the next time this device starts
# SaveLocker" is true and unusable: nothing on a Deck says that means a systemd --user unit.
out="$(prefix_agent doctor)"
check "doctor says what actually applies it"           "$(contains "${out}" "Restart your device to install it")"
# Both lingering branches must carry this clause and must carry it identically — the first draft
# had one of them say "— restarting Steam" mid-sentence, so this check silently only held on a
# non-lingering box and would have failed on a Deck that took the KB's enable-linger advice.
check "doctor kills the wrong guess about Steam"       "$(contains "${out}" "Restarting Steam will not")"
# Desktop mode and back is a logout/login, so it cycles the user manager — but only while lingering
# is off. Three KB articles recommend `loginctl enable-linger`, and a user who took that advice must
# not be told to do something that does nothing. Asserted against whichever state this box is in.
if [ -e "/var/lib/systemd/linger/$(id -un)" ]; then
  check "lingering on: the mode switch is NOT promised" \
    "$(grep -qF -- 'Desktop mode' <<<"${out}" && echo 1 || echo 0)"
else
  check "lingering off: the mode switch is offered too" "$(contains "${out}" "switch to Desktop mode and back")"
fi

# ---- With the game closed, it applies ----
out="$(prefix_agent apply-update)"
# "installed (" and not just "installed": the ROLLBACK message is "…was installed but never started
# successfully", so the looser match reported a successful install for a run that had just undone
# one. It passed for exactly the wrong reason while the checks around it failed.
check "apply installs the staged update"               "$(contains "${out}" "installed (")"
check "the new agent is in place"                      "$("${fake_prefix}/savelocker" version 2>/dev/null | grep -qx '9.9.9' && echo 0 || echo 1)"
check "a file only the new package carries arrived"    "$([ -f "${fake_prefix}/newfile.txt" ] && echo 0 || echo 1)"
# The whole reason apply copies file-by-file instead of swapping the directory.
check "config.json survived the update"                "$([ -f "${prefix_cfg}" ] && echo 0 || echo 1)"
check "the machine API key survived the update" \
  "$([ "$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["ApiKey"])' "${prefix_cfg}")" = "${orig_key}" ] && echo 0 || echo 1)"
check "the previous version is kept for rollback"      "$([ -f "${fake_prefix}/update/previous/savelocker" ] && echo 0 || echo 1)"
check "the update is not confirmed yet"                "$([ -f "${fake_prefix}/update/applied.json" ] && echo 0 || echo 1)"

# ---- An update that never starts is rolled back ----
# applied.json surviving into the next start IS the failure signal: the agent was replaced and never
# reached the point where it could confirm itself. This is the case that decides whether a bad
# release costs a Deck owner an evening or their whole install.
out="$(prefix_agent apply-update)"
check "an unconfirmed update is rolled back"           "$(contains "${out}" "rolled back")"
check "the previous agent binary is back"              "$([ "$(md5sum "${fake_prefix}/savelocker" | cut -d' ' -f1)" = "${orig_sum}" ] && echo 0 || echo 1)"
check "the rollback cleared its own marker"            "$([ ! -f "${fake_prefix}/update/applied.json" ] && echo 0 || echo 1)"
check "the rollback did not touch config.json" \
  "$([ "$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["ApiKey"])' "${prefix_cfg}")" = "${orig_key}" ] && echo 0 || echo 1)"

# ---- A daemon that comes up confirms the update, and the rollback set is released ----
dotnet "${fake_prefix}/savelocker.dll" daemon --config "${prefix_cfg}" --port 5187 \
  >"${scratch}/commit-daemon.log" 2>&1 &
commit_pid=$!
for _ in $(seq 1 40); do
  [ -f "${fake_prefix}/update/apply.json" ] && break
  sleep 0.5
done
kill "${commit_pid}" 2>/dev/null; wait "${commit_pid}" 2>/dev/null
prefix_agent apply-update >/dev/null
check "re-staged and re-applied"                       "$([ -f "${fake_prefix}/update/applied.json" ] && echo 0 || echo 1)"

dotnet "${fake_prefix}/savelocker.dll" daemon --config "${prefix_cfg}" --port 5187 \
  >"${scratch}/commit-daemon2.log" 2>&1 &
commit_pid=$!
for _ in $(seq 1 40); do
  [ ! -f "${fake_prefix}/update/applied.json" ] && break
  sleep 0.5
done
kill "${commit_pid}" 2>/dev/null; wait "${commit_pid}" 2>/dev/null
check "a daemon that starts confirms the update"       "$([ ! -f "${fake_prefix}/update/applied.json" ] && echo 0 || echo 1)"
check "the rollback copy is released once confirmed"   "$([ ! -d "${fake_prefix}/update/previous" ] && echo 0 || echo 1)"

# ---- The console is told, because on a Deck nothing else can tell anyone ----
# The events file is the durable half of health reporting: apply-update runs from ExecStartPre and
# exits long before a heartbeat is possible, so it writes here and the daemon starting straight
# afterwards drains it. Without these codes an update — or a rollback — is completely invisible.
events="$(cat "${fake_prefix}/health-events.json" 2>/dev/null || echo '{}')"
check "staging an update is reported to the console"   "$(contains "${events}" "update.staged")"
check "a rollback is reported to the console"          "$(contains "${events}" "update.rolled_back")"
check "the rollback event is an error, not a whisper"  "$(python3 -c '
import json,sys
d=json.load(open(sys.argv[1]))
ev=[e for e in d.get("Events",[]) if e.get("Code")=="update.rolled_back"]
sys.exit(0 if ev and ev[0].get("Severity") in (2,"Error") else 1)' "${fake_prefix}/health-events.json" && echo 0 || echo 1)"

# ---- A package that cannot be prepared is reported too ----
# The machine keeps working, so nothing else looks wrong; it simply stops updating. That is exactly
# the failure a headless device never surfaces on its own.
rm -f "${fake_prefix}/health-events.json"
make_agent_tarball "${scratch}/broken2.tar.gz" "9.9.9" 1
host_tarball "${scratch}/broken2.tar.gz" "9.9.9"
prefix_agent update >/dev/null 2>&1
events="$(cat "${fake_prefix}/health-events.json" 2>/dev/null || echo '{}')"
check "a refused package is reported to the console"   "$(contains "${events}" "update.failed")"

# ---- AutoUpdate: false stops the agent preparing updates by itself ----
# It must keep CHECKING, though: the point is to choose when the files change, not to stop being
# told you are behind. So the daemon still reports an available version to its own API.
rm -rf "${fake_prefix}/update"
make_agent_tarball "${scratch}/good2.tar.gz" "9.9.9"
host_tarball "${scratch}/good2.tar.gz" "9.9.9"
python3 - "${prefix_cfg}" <<'PY4'
import json,sys
p=sys.argv[1]; c=json.load(open(p)); c["AutoUpdate"]=False; json.dump(c,open(p,"w"))
PY4
dotnet "${fake_prefix}/savelocker.dll" daemon --config "${prefix_cfg}" --port 5187 \
  >"${scratch}/noauto-daemon.log" 2>&1 &
noauto_pid=$!
noauto_json=""
for _ in $(seq 1 20); do
  noauto_json="$(curl -sf -H "X-SaveLocker-Token: $(cat "${fake_prefix}/api-token")" \
    "http://localhost:5187/api/agent-version" 2>/dev/null)"
  grep -q '"updateAvailable":true' <<<"${noauto_json}" && break
  sleep 1
done
kill "${noauto_pid}" 2>/dev/null; wait "${noauto_pid}" 2>/dev/null
check "AutoUpdate:false still reports being behind"    "$(contains "${noauto_json}" '"updateAvailable":true')"
check "AutoUpdate:false stages nothing"                "$([ ! -f "${fake_prefix}/update/apply.json" ] && echo 0 || echo 1)"

# ── The Decky plugin: the agent keeps it updated ──────────────────────────────────────────────
# Phase 5 of tasks/DeckyPlugin.md. No Decky and no Steam Deck needed — the plugin directory is just
# a directory, which is the same trick the whole harness uses for Steam. What is being tested is the
# agent's half: version comparison, digest verification, and above all the plan-before-write rule
# that keeps a package it cannot fully install from being half-installed.
echo
echo "==> Decky plugin updates"

plugin_dir="${HOME}/homebrew/plugins/SaveLocker"
plugin_ver() { python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "${plugin_dir}/package.json" 2>/dev/null; }
# The events file follows the CONFIG, not HOME: HealthReporter writes into config.StateDir, which
# for deck_cfg is the scratch root (the same place its api-token sits), NOT the fake home's default
# state dir. Reading the default one finds nothing and looks exactly like "the event was never
# reported" — which is what it looked like the first time this block ran.
plugin_events() { cat "${scratch}/health-events.json" 2>/dev/null || echo '{}'; }

# Builds a plugin zip: $1 out, $2 version, $3 marker written into dist/index.js,
# $4 (optional) an extra top-level path the package needs.
make_plugin_zip() {
  python3 - "$@" <<'PYZIP'
import json, sys, zipfile
out, ver, marker = sys.argv[1], sys.argv[2], sys.argv[3]
extra = sys.argv[4] if len(sys.argv) > 4 else None
with zipfile.ZipFile(out, "w") as z:
    z.writestr("SaveLocker/package.json", json.dumps({"name": "SaveLocker", "version": ver}))
    z.writestr("SaveLocker/plugin.json", json.dumps({"name": "SaveLocker", "version": ver, "flags": ["debug"]}))
    z.writestr("SaveLocker/main.py", "# plugin %s\n" % ver)
    z.writestr("SaveLocker/dist/index.js", "// %s\n" % marker)
    if extra:
        z.writestr("SaveLocker/" + extra, "payload the old layout has no home for\n")
PYZIP
}
host_plugin() {   # $1 zip, $2 version
  curl -sf -X POST "${server_url}/api/admin/agent-installer?version=$2&platform=decky-plugin" \
    -F "file=@$1;filename=SaveLocker.zip" >/dev/null
}

# ---- No Decky at all: silent, and never an error ----
# Almost every machine. A plugin updater that complains on a box that has never heard of Decky is
# one an admin learns to ignore, taking the real reports with it.
out="$(agent plugin-update --config "${deck_cfg}")"; plugin_rc=$?
check "no Decky: plugin-update exits 0"           "${plugin_rc}"
check "no Decky: it says why"                     "$(contains "${out}" "Decky Loader is not installed")"
out="$(agent doctor --config "${deck_cfg}")"; doctor_nodecky_rc=$?
check "no Decky: doctor says nothing applies"     "$(contains "${out}" "Decky Loader is not installed")"
check "no Decky: doctor does not call it a problem" "${doctor_nodecky_rc}"

# ---- Decky present, plugin not installed: the one thing the agent cannot do ----
# Creating the plugin directory needs root, so first install stays a manual paste. Doctor is where
# a user finds that out, so it has to carry the URL.
mkdir -p "${HOME}/homebrew/plugins"
out="$(agent doctor --config "${deck_cfg}")"
check "Decky without the plugin: doctor names the plugin" "$(contains "${out}" "plugin is not installed")"
check "Decky without the plugin: doctor gives the install URL" \
  "$(contains "${out}" "SaveLocker-Decky/releases/latest/download")"

# ---- An installed plugin at v0.1.0 ----
# plugin.json is root-owned on a real device and must never be written; here it carries a sentinel
# so "left exactly as it was" is an assertion rather than a hope. stale.js is a file the NEW package
# does not carry, which dist/ pruning must remove.
mkdir -p "${plugin_dir}/dist"
echo '{"name":"SaveLocker","version":"0.1.0"}' >"${plugin_dir}/package.json"
echo '{"name":"SaveLocker","version":"0.1.0","flags":["debug"],"sentinel":"do-not-touch"}' >"${plugin_dir}/plugin.json"
echo '# plugin 0.1.0' >"${plugin_dir}/main.py"
echo '// old bundle' >"${plugin_dir}/dist/index.js"
echo '// removed in 0.2.0' >"${plugin_dir}/dist/stale.js"

make_plugin_zip "${scratch}/plugin-0.2.0.zip" "0.2.0" "bundle 0.2.0"
host_plugin "${scratch}/plugin-0.2.0.zip" "0.2.0"

# ---- --check reports and installs nothing ----
out="$(agent plugin-update --check --config "${deck_cfg}")"; plugin_rc=$?
check "--check sees the newer plugin"             "$(contains "${out}" "v0.2.0 available")"
check "--check exits non-zero when behind"        "$([ "${plugin_rc}" != "0" ] && echo 0 || echo 1)"
check "--check installed nothing"                 "$([ "$(plugin_ver)" = "0.1.0" ] && echo 0 || echo 1)"

# ---- The install ----
out="$(agent plugin-update --config "${deck_cfg}")"; plugin_rc=$?
check "plugin-update installs the new version"    "${plugin_rc}"
check "package.json now reports 0.2.0"            "$([ "$(plugin_ver)" = "0.2.0" ] && echo 0 || echo 1)"
check "the new bundle is in place"                "$(contains "$(cat "${plugin_dir}/dist/index.js")" "bundle 0.2.0")"
check "main.py was replaced"                      "$(contains "$(cat "${plugin_dir}/main.py")" "plugin 0.2.0")"
# The correction from the Phase 4 hardware pass: Decky chowns plugin.json to root even for a
# non-_root plugin, so the agent must not depend on writing it — and must not quietly try.
check "plugin.json was NOT touched"               "$(contains "$(cat "${plugin_dir}/plugin.json")" "do-not-touch")"
check "a dist file the new version dropped is gone" \
  "$([ ! -f "${plugin_dir}/dist/stale.js" ] && echo 0 || echo 1)"
check "the console is told the plugin was updated" "$(contains "$(plugin_events)" "plugin.updated")"

# ---- A second pass changes nothing ----
out="$(agent plugin-update --config "${deck_cfg}")"; plugin_rc=$?
check "a second pass reports up to date"          "$(contains "${out}" "up to date")"
check "a second pass exits 0"                     "${plugin_rc}"

# ---- An OLDER package on the server never downgrades an installation ----
make_plugin_zip "${scratch}/plugin-0.0.9.zip" "0.0.9" "ancient bundle"
host_plugin "${scratch}/plugin-0.0.9.zip" "0.0.9"
out="$(agent plugin-update --config "${deck_cfg}")"
check "an older hosted plugin does not downgrade"  "$([ "$(plugin_ver)" = "0.2.0" ] && echo 0 || echo 1)"
check "the bundle is still the newer one"          "$(contains "$(cat "${plugin_dir}/dist/index.js")" "bundle 0.2.0")"

# ---- A wrong digest is refused, and nothing is written ----
# The plugin becomes code running inside Steam's own UI, and plain http proves nothing about where
# it came from (Decisions.md), so the digest is the only integrity control there is. Rewriting the
# stored file behind the server's back is exactly what a substituted download looks like.
make_plugin_zip "${scratch}/plugin-0.3.0.zip" "0.3.0" "bundle 0.3.0"
host_plugin "${scratch}/plugin-0.3.0.zip" "0.3.0"
make_plugin_zip "${scratch}/plugin-tampered.zip" "0.3.0" "TAMPERED"
cp "${scratch}/plugin-tampered.zip" "${inst_root}/decky-plugin/SaveLocker.zip"
out="$(agent plugin-update --config "${deck_cfg}")"; plugin_rc=$?
check "a tampered plugin package is REFUSED"       "$(contains "${out}" "checksum")"
check "the refusal exits non-zero"                 "$([ "${plugin_rc}" != "0" ] && echo 0 || echo 1)"
check "the tampered package installed nothing"     "$([ "$(plugin_ver)" = "0.2.0" ] && echo 0 || echo 1)"
check "the tampered bundle never landed"           "$(grep -q 'TAMPERED' "${plugin_dir}/dist/index.js" && echo 1 || echo 0)"
check "a refused plugin update is reported to the console" \
  "$(contains "$(plugin_events)" "plugin.update_failed")"

# ---- A package needing a file the agent may not create is refused BEFORE any write ----
# The real constraint: the plugin directory itself is root-owned 755, so an existing file can be
# overwritten but a new top-level one cannot be created. Discovering that partway through would
# leave a plugin half on one version and half on another, which is worse than an old one.
make_plugin_zip "${scratch}/plugin-0.4.0.zip" "0.4.0" "bundle 0.4.0" "py_modules/helper.py"
host_plugin "${scratch}/plugin-0.4.0.zip" "0.4.0"
chmod 555 "${plugin_dir}"
out="$(agent plugin-update --config "${deck_cfg}")"; plugin_rc=$?
chmod 755 "${plugin_dir}"
check "a package needing a new top-level file is REFUSED" "$(contains "${out}" "py_modules/helper.py")"
check "the refusal names the reinstall route"      "$(contains "${out}" "Install Plugin from")"
check "the refusal exits non-zero"                 "$([ "${plugin_rc}" != "0" ] && echo 0 || echo 1)"
check "the refused package wrote NOTHING"          "$([ "$(plugin_ver)" = "0.2.0" ] && echo 0 || echo 1)"
check "not even the file it could have written"    "$(contains "$(cat "${plugin_dir}/dist/index.js")" "bundle 0.2.0")"

# ---- AutoUpdate:false reports but does not apply ----
# The same switch that governs the agent's own updates, for the same reason: turning it off means
# nothing gets replaced without someone deciding, not that you stop being told you are behind.
make_plugin_zip "${scratch}/plugin-0.5.0.zip" "0.5.0" "bundle 0.5.0"
host_plugin "${scratch}/plugin-0.5.0.zip" "0.5.0"
noauto_plugin_cfg="${scratch}/plugin-noauto-config.json"
python3 - "${deck_cfg}" "${noauto_plugin_cfg}" <<'PYNOAUTO'
import json, sys
c = json.load(open(sys.argv[1])); c["AutoUpdate"] = False; json.dump(c, open(sys.argv[2], "w"))
PYNOAUTO
out="$(agent plugin-update --config "${noauto_plugin_cfg}")"
check "AutoUpdate:false still reports the newer plugin" "$(contains "${out}" "v0.5.0 available")"
check "AutoUpdate:false says why nothing happened"      "$(contains "${out}" "Automatic updates are turned off")"
check "AutoUpdate:false installed nothing"              "$([ "$(plugin_ver)" = "0.2.0" ] && echo 0 || echo 1)"

# ---- doctor carries the plugin's state, because on a Deck it is the only UI ----
out="$(agent doctor --config "${deck_cfg}")"; doctor_plugin_rc=$?
check "doctor reports the installed plugin version" "$(contains "${out}" "installed: v0.2.0")"
check "doctor reports the available plugin update"  "$(contains "${out}" "v0.5.0 available")"
check "a plugin update is not a doctor problem"     "${doctor_plugin_rc}"

rm -rf "${HOME}/homebrew"

# ── The generated systemd unit and the packaged one are the same text ─────────────────────────
# They are two writers of one file and they had drifted: the packaged unit carried the update hook
# and the hardening, the generated one did not, so whether a Deck got them depended on which had
# last written it.
unit_out="${HOME}/.config/systemd/user/savelocker.service"
rm -f "${unit_out}"
# A stub systemctl, so a WSL box that really does run systemd does not end up with an enabled user
# service pointing into a test directory. The unit FILE is written before systemctl is ever called,
# which is the whole point of the checks below.
mkdir -p "${scratch}/nosystemctl"
printf '#!/usr/bin/env bash\nexit 1\n' >"${scratch}/nosystemctl/systemctl"
chmod +x "${scratch}/nosystemctl/systemctl"
PATH="${scratch}/nosystemctl:${PATH}" prefix_agent autostart --enable >/dev/null 2>&1 || true
if [ -f "${unit_out}" ]; then
  check "the generated unit carries the update hook"   "$(grep -q 'apply-update' "${unit_out}" && echo 0 || echo 1)"
  check "the generated unit carries the hardening"     "$(grep -q 'NoNewPrivileges=yes' "${unit_out}" && echo 0 || echo 1)"
  check "the generated unit points at this agent"      "$(grep -q "${fake_prefix}" "${unit_out}" && echo 0 || echo 1)"
  # ProtectHome would cut the agent off from every save file it exists to sync. Anchored, because
  # the unit explains in a comment WHY it is absent — and an unanchored grep matches that comment,
  # so the check failed on a unit that was perfectly correct.
  check "the generated unit does not set ProtectHome"  "$(grep -qE '^ProtectHome' "${unit_out}" && echo 1 || echo 0)"
else
  check "the generated unit was written"               "1"
fi

# ── The update channel: AgentUpdate:Linux:* and the off-origin refusal ───────────────────────
# A second server whose Linux slot is EMPTY, so the config fallback is the only source — the one
# path that proves AgentUpdate:Linux:* is read at all, since an empty section is indistinguishable
# from a missing one when the answer is 204 either way. Its DownloadUrl points somewhere that is
# not this server and carries no digest, which must be refused before a single request is made.
alt_port=5196
alt_url="http://127.0.0.1:${alt_port}"
foreign_port=5997
alt_state="${scratch}/alt-server"
mkdir -p "${alt_state}/archives"
ASPNETCORE_URLS="${alt_url}" \
Storage__DbPath="${alt_state}/savelocker.db" \
Storage__ArchiveRoot="${alt_state}/archives" \
Storage__AgentInstallerRoot="${alt_state}/agent-installer" \
AgentUpdate__Linux__LatestVersion="9.9.9" \
AgentUpdate__Linux__DownloadUrl="http://127.0.0.1:${foreign_port}/savelocker-9.9.9-linux-x64.tar.gz" \
  dotnet run --project "${repo_root}/src/Server/SaveLocker.Server.csproj" \
    --no-launch-profile >"${scratch}/alt-server.log" 2>&1 &
alt_pid=$!
for _ in $(seq 1 60); do
  curl -sf "${alt_url}/api/admin/status" >/dev/null 2>&1 && break
  sleep 1
done

alt_cfg="${scratch}/alt-config.json"
python3 - "${alt_cfg}" "${alt_url}" <<'PY3'
import json,sys
json.dump({"ServerUrl": sys.argv[2], "MachineName": "DeckAlt", "Games": []}, open(sys.argv[1], "w"))
PY3
agent register --config "${alt_cfg}" --name "DeckAlt" >/dev/null

out="$(agent check-update --config "${alt_cfg}")"
check "AgentUpdate:Linux:* is served to a Linux agent"  "$(contains "${out}" "v9.9.9 is available")"
out="$(agent check-update --download --config "${alt_cfg}")"
check "an off-origin update with no digest is REFUSED"  "$(contains "${out}" "REFUSED")"
check "the refusal names the foreign host"              "$(contains "${out}" "127.0.0.1")"
# Nothing is listening on ${foreign_port} and nothing ever will be. That wording only comes from
# the refusal raised BEFORE any request is made — a connection failure would read completely
# differently — so this is what proves the agent never reached out, without needing a listener to
# ask. (WA-05 makes the same point on Windows with a real listener that records its hits.)
check "the refusal is the pre-request one, not a failed fetch" \
  "$(contains "${out}" "Refusing to download an update")"

kill "${alt_pid}" 2>/dev/null
wait "${alt_pid}" 2>/dev/null

# what. The failure is not hypothetical on a Deck: reached over SSH without a login session there
# is no user bus, systemctl fails, and the agent cheerfully said the service was off while it kept
# running. A stub systemctl that exits non-zero reproduces exactly that, without needing to break
# the real one. It must come FIRST on PATH but must not hide dotnet.
fake_bin="${scratch}/fakebin"
mkdir -p "${fake_bin}"
cat >"${fake_bin}/systemctl" <<'STUB'
#!/usr/bin/env bash
echo "Failed to connect to bus: No medium found" >&2
exit 1
STUB
chmod +x "${fake_bin}/systemctl"

# The unit file must exist, or SetEnabled never reaches systemctl at all.
mkdir -p "${HOME}/.config/systemd/user"
touch "${HOME}/.config/systemd/user/savelocker.service"

out="$(PATH="${fake_bin}:${PATH}" agent autostart --disable)"
autostart_rc=$?
check "autostart --disable fails loudly when systemctl fails"   "$([ "${autostart_rc}" != "0" ] && echo 0 || echo 1)"
check "autostart --disable does not claim success"   "$(grep -q 'Auto-start disabled' <<<"${out}" && echo 1 || echo 0)"

# ---- Launch options: the rewrite rule (tasks/DeckyPlugin.md Phase 1) ----
#
# Pasting `savelocker run -- %command%` into Steam is the last manual per-device step, and the one
# nothing notices was skipped. The rule that automates it has to SUBSTITUTE into %command% rather
# than append, because users run mangohud, set env vars and pass per-game arguments — each of which
# has a position Steam expects it in. It also has to be idempotent: it runs on a timer, not once.
#
# No Steam here, and none needed: the rule is pure, and `launch-options --wrapper` drives it
# directly. What Steam does with the result is Phase 3, on hardware.
echo
echo "==> Launch options"

w="/home/deck/.local/share/SaveLocker/savelocker"
inv="${w} run -- %command%"
lo() { agent launch-options --wrapper "$1" --preview "$2"; }

check "no existing options -> the wrapper invocation" \
  "$([ "$(agent launch-options --wrapper "${w}")" = "${inv}" ] && echo 0 || echo 1)"
check "empty existing options -> the wrapper invocation" \
  "$([ "$(lo "${w}" "")" = "${inv}" ] && echo 0 || echo 1)"

# The three shapes a real user's launch options actually take.
check "an outer wrapper is preserved, not clobbered" \
  "$([ "$(lo "${w}" "mangohud %command%")" = "mangohud ${inv}" ] && echo 0 || echo 1)"
check "a leading env assignment stays in front" \
  "$([ "$(lo "${w}" "DXVK_HUD=1 %command%")" = "DXVK_HUD=1 ${inv}" ] && echo 0 || echo 1)"
# Steam appends a %command%-less string to the game's own argv, which is exactly where it lands.
check "game arguments with no %command% end up after it" \
  "$([ "$(lo "${w}" "-novid -fullscreen")" = "${inv} -novid -fullscreen" ] && echo 0 || echo 1)"

# THE repair, and the ONLY one. A hand-typed short command is the documented Game-Mode failure:
# ~/.local/bin is not on PATH there, so the game silently never launches. Only the path changes;
# everything else survives.
check "a bare 'savelocker' is repaired to the absolute path" \
  "$([ "$(lo "${w}" "savelocker run -- %command%")" = "${inv}" ] && echo 0 || echo 1)"

# An absolute path is left ALONE even when it is not the one we would have written. Measured on a
# real Deck (2026-08-15): install.sh symlinks ~/.local/bin/savelocker to the binary under
# ~/.local/share, and games set up at different times carry different spellings. Both run the same
# program, so rewriting one to the other is churn on a working config, not a repair — and it would
# have edited two of the maintainer's real games on the plugin's first write.
other="/home/deck/.local/bin/savelocker"
check "a different ABSOLUTE savelocker path is left alone" \
  "$([ "$(lo "${w}" "${other} run -- %command%")" = "${other} run -- %command%" ] && echo 0 || echo 1)"
# The exact string off the Deck, WINEDLLOVERRIDES and all: quotes, commas and '=' inside the value.
real='WINEDLLOVERRIDES="winmm,version,dxgi=n,b" /home/deck/.local/bin/savelocker run -- %command%'
check "a real Deck game's options survive untouched" \
  "$([ "$(lo "${w}" "${real}")" = "${real}" ] && echo 0 || echo 1)"

# Idempotent: the timer re-runs this against its own output every few minutes.
for existing in "" "mangohud %command%" "-novid" "savelocker run -- %command%" \
                "${real}" "/home/deck/.local/bin/savelocker run -- %command%"; do
  once="$(lo "${w}" "${existing}")"
  twice="$(lo "${w}" "${once}")"
  check "idempotent for '${existing:-(empty)}'" "$([ "${once}" = "${twice}" ] && echo 0 || echo 1)"
done

# Idempotence must not depend on the binary being NAMED savelocker. Run from a build tree the agent
# is `dotnet savelocker.dll`, so the resolved wrapper is the dotnet host — and a rule keyed on the
# name alone does not recognise its own output and wraps it again on every pass.
odd="/usr/lib/dotnet/dotnet"
odd_once="$(lo "${odd}" "")"
check "idempotent for a wrapper not named savelocker" \
  "$([ "$(lo "${odd}" "${odd_once}")" = "${odd_once}" ] && echo 0 || echo 1)"

# A non-default install prefix can contain spaces; unquoted, Steam would run the first word and pass
# the rest as arguments.
spaced="/home/deck/My Games/savelocker"
spaced_inv="\"${spaced}\" run -- %command%"
check "a wrapper path with spaces is quoted" \
  "$([ "$(agent launch-options --wrapper "${spaced}")" = "${spaced_inv}" ] && echo 0 || echo 1)"
check "an already-applied quoted path is left alone" \
  "$([ "$(lo "${spaced}" "${spaced_inv}")" = "${spaced_inv}" ] && echo 0 || echo 1)"

# Only OUR wrapper is rewritten. Another tool that happens to sit in front of %command% is a user's
# deliberate choice, not something to repair.
check "a non-savelocker wrapper is not mistaken for a stale one" \
  "$([ "$(lo "${w}" "gamemoderun %command%")" = "gamemoderun ${inv}" ] && echo 0 || echo 1)"

# ---- A game mapped inside a prefix but with no recorded AppID ----
#
# Found on real hardware 2026-08-15, on two of four tracked games. The wrapper matched only the
# STORED SteamAppId, so such a game matched nothing: it logged "no tracked game matches this launch"
# and played the entire session with no sync, while the game was tracked, its save folder mapped and
# its launch options correct. The maintainer's Khazan save from 2026-08-11 was never pushed.
#
# A prefix directory is named for the AppID that launches into it, so the save path already answers
# this. The wrapper resolves it; the daemon records it.
echo
echo "==> AppID recovered from the save path"

noappid_dir="${scratch}/no-appid"
mkdir -p "${noappid_dir}"
noappid_cfg="${noappid_dir}/config.json"
cat >"${noappid_cfg}" <<EOF
{
  "ServerUrl": "${server_url}",
  "ManifestCachePath": "${fixtures}/manifest.yaml",
  "Games": [
    { "GameId": "33333333-3333-3333-3333-333333333333", "Name": "Prefix Game No AppId",
      "SaveDirectory": "${PREFIX_SAVE}" }
  ]
}
EOF

out="$(agent doctor --config "${noappid_cfg}")"
check "doctor recovers the appid from the save path" \
  "$(contains "${out}" "${PREFIX_APPID} (from the save path")"
check "doctor does not call it unmatched"  "$(grep -q 'the launch wrapper cannot match' <<<"${out}" && echo 1 || echo 0)"

# The launch itself: exactly the shape that silently synced nothing.
export STEAM_COMPAT_DATA_PATH="${PREFIX}"
export SteamAppId="${PREFIX_APPID}"
out="$(agent run --config "${noappid_cfg}" -- "${fixtures}/slow-game.sh" "${PREFIX_SAVE}" 1 0)"
unset STEAM_COMPAT_DATA_PATH SteamAppId
check "the wrapper matches a game that never recorded an appid" \
  "$(grep -q 'no tracked game matches this launch' <<<"${out}" && echo 1 || echo 0)"

# And the daemon writes it down, so config.json, `list` and the console stop lying about it.
dotnet "${agent_dir}/bin/Debug/net10.0/savelocker.dll" daemon \
  --config "${noappid_cfg}" --port 5186 >"${noappid_dir}/daemon.log" 2>&1 &
bf_pid=$!
for _ in $(seq 1 40); do
  curl -sf "http://localhost:5186/" >/dev/null 2>&1 && break
  sleep 0.5
done
kill "${bf_pid}" 2>/dev/null; wait "${bf_pid}" 2>/dev/null
check "the daemon records the recovered appid in config" \
  "$(grep -q "\"SteamAppId\": \"${PREFIX_APPID}\"" "${noappid_cfg}" && echo 0 || echo 1)"

# ---- Launch options: the desired-state API (tasks/DeckyPlugin.md Phase 2) ----
#
# The agent publishes what each game SHOULD carry and takes a report back; something that can write
# Steam's launch options does the writing. Nothing here needs Steam, Decky or a Deck.
echo
echo "==> Launch options API"

lo_dir="${scratch}/launch-options"
mkdir -p "${lo_dir}"
lo_cfg="${lo_dir}/config.json"
# Two games on purpose: one Steam launches, one it does not. The AppID is written in Steam's SIGNED
# form, which is what a user copying from another tool would type — the API must hand back the
# unsigned form Steam's own JS API uses, or every lookup silently matches nothing.
cat >"${lo_cfg}" <<EOF
{
  "ServerUrl": "${server_url}",
  "ManifestCachePath": "${fixtures}/manifest.yaml",
  "Games": [
    { "GameId": "11111111-1111-1111-1111-111111111111", "Name": "Prefix Game",
      "SaveDirectory": "${PREFIX_SAVE}", "SteamAppId": "-1234567890" },
    { "GameId": "22222222-2222-2222-2222-222222222222", "Name": "No AppId Game",
      "SaveDirectory": "${PORTABLE_SAVE}" }
  ]
}
EOF

dotnet "${agent_dir}/bin/Debug/net10.0/savelocker.dll" daemon \
  --config "${lo_cfg}" --port 5187 >"${lo_dir}/daemon.log" 2>&1 &
lo_pid=$!
for _ in $(seq 1 40); do
  curl -sf "http://localhost:5187/" >/dev/null 2>&1 && break
  sleep 0.5
done
lo_token="$(cat "${lo_dir}/api-token")"
lo_api="http://localhost:5187/api/launch-options"
get() { curl -sf -H "X-SaveLocker-Token: ${lo_token}" "$@"; }

rows="$(get "${lo_api}")"
check "a tracked game with an AppID gets a row"      "$(contains "${rows}" '"name":"Prefix Game"')"
check "the AppID is emitted UNSIGNED"                "$(contains "${rows}" "\"steamAppId\":${PREFIX_APPID}")"
check "a game with no AppID gets no row"             "$(grep -q 'No AppId Game' <<<"${rows}" && echo 1 || echo 0)"
check "the row carries the wrapper invocation"        "$(contains "${rows}" 'run -- %command%')"
# Nothing has reported yet, and that is UNKNOWN rather than broken — most machines have nothing that
# could report. Saying "not applied" here would make the signal worthless.
check "an unreported game is unknown, not failed"    "$(contains "${rows}" '"appliedAt":null')"

# The management API hands out control of this machine; these two are the whole reason a browser
# tab cannot drive it (LocalAuth). Asserted here because a new route is a new chance to forget.
code="$(curl -s -o /dev/null -w '%{http_code}' "${lo_api}")"
check "the endpoint refuses an unauthenticated caller" "$([ "${code}" = "401" ] && echo 0 || echo 1)"
# 403, not 401: a page that already has the token must still be refused on Origin alone.
code="$(curl -s -o /dev/null -w '%{http_code}' -H "X-SaveLocker-Token: ${lo_token}" \
        -H "Origin: https://evil.example" "${lo_api}")"
check "the endpoint refuses a foreign Origin"         "$([ "${code}" = "403" ] && echo 0 || echo 1)"

# ---- /api/decky: what the agent UI reads to decide what to say about the plugin ----
# Local file reads only, and re-read per request — the plugin can be installed, updated or removed
# while the daemon runs, including by the agent itself. A cached answer would tell a user to install
# something they just installed.
decky_api="http://localhost:5187/api/decky"

rm -rf "${HOME}/homebrew"
d="$(get "${decky_api}")"
check "decky: applicable on Linux"                   "$(contains "${d}" '"applicable":true')"
check "decky: absent Decky is reported, not guessed" "$(contains "${d}" '"deckyPresent":false')"
check "decky: no plugin without Decky"               "$(contains "${d}" '"pluginInstalled":false')"
check "decky: the install URL has one source"        "$(contains "${d}" 'SaveLocker-Decky/releases/latest/download')"

mkdir -p "${HOME}/homebrew/plugins"
d="$(get "${decky_api}")"
check "decky: Decky alone is detected"               "$(contains "${d}" '"deckyPresent":true')"
check "decky: Decky alone is not a plugin"           "$(contains "${d}" '"pluginInstalled":false')"

mkdir -p "${HOME}/homebrew/plugins/SaveLocker"
echo '{"name":"SaveLocker","version":"0.2.0"}' >"${HOME}/homebrew/plugins/SaveLocker/package.json"
d="$(get "${decky_api}")"
check "decky: an installed plugin is seen LIVE"      "$(contains "${d}" '"pluginInstalled":true')"
# Read from the same package.json Decky reports in its own UI, so the two cannot disagree.
check "decky: it reports the installed version"      "$(contains "${d}" '"pluginVersion":"0.2.0"')"

# An unreadable package.json is the reinstall signal, not a crash.
echo 'not json at all' >"${HOME}/homebrew/plugins/SaveLocker/package.json"
d="$(get "${decky_api}")"
check "decky: an unreadable package.json is null, not a 500" "$(contains "${d}" '"pluginVersion":null')"

# Same two guards as every other route on this API — a new route is a new chance to forget them.
code="$(curl -s -o /dev/null -w '%{http_code}' "${decky_api}")"
check "decky: refuses an unauthenticated caller"     "$([ "${code}" = "401" ] && echo 0 || echo 1)"
code="$(curl -s -o /dev/null -w '%{http_code}' -H "X-SaveLocker-Token: ${lo_token}" \
        -H "Origin: https://evil.example" "${decky_api}")"
check "decky: refuses a foreign Origin"              "$([ "${code}" = "403" ] && echo 0 || echo 1)"

rm -rf "${HOME}/homebrew"

# The merge. Only the caller knows the CURRENT options and only the agent knows the rule, which is
# why this is a round trip rather than a string the caller assembles.
resolved="$(get -X POST -H 'Content-Type: application/json' \
  -d "{\"games\":[{\"steamAppId\":${PREFIX_APPID},\"current\":\"mangohud %command%\"}]}" \
  "${lo_api}/resolve")"
check "resolve wraps an existing mangohud, not clobbers it" "$(contains "${resolved}" 'mangohud ')"
check "resolve reports it as changed"                       "$(contains "${resolved}" '"changed":true')"

already="$(python3 -c "import json,sys; print(json.load(sys.stdin)[0]['desired'])" <<<"${rows}")"
resolved="$(get -X POST -H 'Content-Type: application/json' \
  -d "$(python3 -c "import json,sys; print(json.dumps({'games':[{'steamAppId':${PREFIX_APPID},'current':sys.argv[1]}]}))" "${already}")" \
  "${lo_api}/resolve")"
check "resolve does not ask to rewrite a correct game"      "$(contains "${resolved}" '"changed":false')"

# --check is the question "is every game confirmed?", so unknown counts against it.
agent launch-options --config "${lo_cfg}" --check >/dev/null 2>&1
lo_check_rc=$?
check "--check fails while nothing is confirmed"      "$([ "${lo_check_rc}" != "0" ] && echo 0 || echo 1)"

out="$(agent doctor --config "${lo_cfg}")"
check "doctor names the unconfirmed game"             "$(contains "${out}" "launch options: unknown")"

# A report comes back. The daemon owns the config while it runs, so it does the write and is stopped
# before anything else reads the file — a second writer here is the documented lost-update trap.
get -X POST -H 'Content-Type: application/json' \
  -d "{\"steamAppId\":${PREFIX_APPID},\"applied\":true}" "${lo_api}/applied" >/dev/null
kill "${lo_pid}" 2>/dev/null; wait "${lo_pid}" 2>/dev/null

agent launch-options --config "${lo_cfg}" --check >/dev/null 2>&1
lo_check_rc=$?
check "--check passes once the game is confirmed"     "$([ "${lo_check_rc}" = "0" ] && echo 0 || echo 1)"
out="$(agent doctor --config "${lo_cfg}")"
check "doctor reports the confirmed game as set"      "$(contains "${out}" "launch options: set (confirmed")"

# A reported FAILURE is a real fault, unlike silence, and doctor must say so.
python3 - "${lo_cfg}" <<'PY'
import json, sys
p = sys.argv[1]
cfg = json.load(open(p))
cfg["Games"][0]["LaunchOptionsAppliedAt"] = None
cfg["Games"][0]["LaunchOptionsError"] = "Steam refused the write"
json.dump(cfg, open(p, "w"), indent=2)
PY
out="$(agent doctor --config "${lo_cfg}")"
doctor_err_rc=$?
check "doctor reports a recorded failure as a problem" "$(contains "${out}" "Steam refused the write")"
check "a recorded failure makes doctor exit non-zero"  "$([ "${doctor_err_rc}" != "0" ] && echo 0 || echo 1)"

echo
echo "==== LINUX AGENT RESULT: ${pass} passed, ${fail} failed ===="
[ "${fail}" = "0" ]
