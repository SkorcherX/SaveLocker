#!/usr/bin/env bash
# Linux agent integration tests — the fake-game harness.
#
# Covers the whole Phase 2 path with no Steam, no Proton, no GPU and no Steam Deck:
#   * shortcuts.vdf discovery, including the signed -> unsigned AppID trap
#   * Proton prefix resolution (manifest tokens resolving INSIDE the prefix)
#   * both save shapes: in-prefix AND portable-next-to-the-exe
#   * the launch wrapper: pull on launch, supervise the child, settle, push on exit
#   * the settle gate waiting out a game that flushes after it exits
#   * the /proc lock probe seeing a held write descriptor
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
ASPNETCORE_URLS="${server_url}" \
Storage__DbPath="${server_state}/savelocker.db" \
Storage__ArchiveRoot="${server_state}/archives" \
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

# ── Prefix resolution: manifest tokens must resolve INSIDE the prefix ────────────────────────
out="$(agent resolve --config "${deck_cfg}" --prefix "${PREFIX}" "Fake Prefix Game")"
check "manifest <winAppData> resolves inside the prefix" "$(contains "${out}" "${PREFIX_SAVE}")"

# The same lookup WITHOUT a prefix must not invent a host path.
out="$(agent resolve --config "${deck_cfg}" "Fake Prefix Game")"
check "no prefix -> no resolution (does not guess a host path)" \
  "$(contains "${out}" "no existing save directory found")"

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

echo
echo "==== LINUX AGENT RESULT: ${pass} passed, ${fail} failed ===="
[ "${fail}" = "0" ]
