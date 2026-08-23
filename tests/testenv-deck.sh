#!/usr/bin/env bash
# Deck half of tests/testenv.ps1 — runs ON the Deck (or any SSH-reachable Linux box) over `ssh`,
# driven from Windows. Never invoked by hand in normal use.
#
# Unlike the WSL target, there is no dotnet here to run a framework-dependent build — SteamOS ships
# no .NET runtime — so this script never builds anything. It only installs a SELF-CONTAINED
# linux-x64 tarball (built in WSL by `testenv.ps1 build -Only deck`, packaging/linux/build-linux.sh
# under the hood) that testenv.ps1 has already `scp`'d to /tmp, and runs it beside — never instead
# of — the real installed agent.
set -uo pipefail

CMD="${1:-status}"

# PowerShell passes a literal '~/savelocker-test' through untouched (single-quoted on the way
# through both hops, so neither side expands it) — expand the leading tilde ourselves rather than
# trust the transport, since $HOME here is real and only this script's own parse of it is.
PREFIX="${SAVELOCKER_DECK_PREFIX:-$HOME/savelocker-test}"
PREFIX="${PREFIX/#\~/$HOME}"
PORT="${SAVELOCKER_DECK_PORT:-5177}"
SERVER_URL="${SAVELOCKER_SERVER_URL:-}"
VERSION="${SAVELOCKER_TEST_VERSION:-0.5.10-test}"
MACHINE="${SAVELOCKER_DECK_MACHINE:-DeckTest}"
TARBALL="/tmp/.savelocker-test.tar.gz"
DAEMON_LOG="$HOME/.savelocker-testenv-deck.log"
BIN="$PREFIX/savelocker"

# The whole isolation mechanism: this points the agent's state at a directory that is never
# ~/.local/share (the real install prefix — Gotchas.md, "the install prefix IS the state
# directory"), so nothing here can touch a real registration, a real tracked game or a real save.
export XDG_DATA_HOME="${PREFIX}-state"
STATE="$XDG_DATA_HOME/SaveLocker"

die() { echo "ERROR: $*" >&2; exit 1; }

# Not a formality: every subcommand below can reach an rm -rf, and PREFIX is built from a value
# Windows passed in. Refuse to operate on anything that doesn't look like a test directory, so a
# blank or mistyped SAVELOCKER_DECK_PREFIX can never resolve to something like $HOME.
case "$PREFIX" in
  *savelocker-test*) ;;
  *) die "refusing to operate — PREFIX '$PREFIX' doesn't look like a test directory" ;;
esac

# Matches only OUR path, never the real install's ~/.local/share/SaveLocker/savelocker — same
# principle as testenv.sh's own DAEMON_PATTERN, just inverted (there it excludes the release
# apphost name; here the isolation is the path itself).
DAEMON_PATTERN="$PREFIX/savelocker daemon"
SCOPE_UNIT="savelocker-testenv-deck"

cmd_install() {
  [ -f "$TARBALL" ] || die "no upload at $TARBALL — push the tarball first"
  echo "== installing to $PREFIX =="
  rm -rf "$PREFIX"
  mkdir -p "$PREFIX"
  tar -xzf "$TARBALL" -C "$PREFIX" --strip-components=1
  rm -f "$TARBALL"
  chmod +x "$BIN"
  echo "installed: $("$BIN" version 2>/dev/null || echo "$VERSION")"
}

# Only ever reports — never untracks a game itself. Which games a test agent syncs is the
# operator's call, not a script's (same reasoning as testenv.ps1's Show-RealGameMappings).
show_real_game_mappings() {
  local cfg="$STATE/config.json"
  [ -f "$cfg" ] || return 0
  # `|| true` matters under `pipefail`: grep exits 1 when NO game maps a real folder - the common,
  # good case - and without it that innocuous "nothing to report" became this function's own exit
  # code, which became cmd_status's/cmd_up's, which testenv.ps1 read as the whole SSH call failing.
  grep -o '"SaveDirectory": *"[^"]*"' "$cfg" | sed -E 's/.*"SaveDirectory": *"([^"]*)"/\1/' |
  while IFS= read -r dir; do
    case "$dir" in
      "$XDG_DATA_HOME"*) ;;
      *) echo "WARNING: the test agent maps a real folder: $dir" >&2
         echo "  a pull from the test console would restore over it." >&2 ;;
    esac
  done || true
}

cmd_up() {
  # A tarball waiting at $TARBALL means testenv.ps1 just pushed one — install it before starting.
  [ -f "$TARBALL" ] && cmd_install
  [ -x "$BIN" ] || die "not installed — run: testenv.ps1 build -Only deck (then up again)"
  cmd_down >/dev/null 2>&1

  [ -n "$SERVER_URL" ] || die "SAVELOCKER_SERVER_URL not set — pass -DeckServerUrl (this PC's LAN IP; the Deck cannot reach 'localhost')"
  if ! curl -sf -m 5 "$SERVER_URL/api/admin/status" >/dev/null 2>&1; then
    die "no test server at $SERVER_URL — start the console first, and make sure it's reachable from the Deck, not just from the PC"
  fi

  # Registering is idempotent from the caller's point of view but rotates the key, so only do it
  # when this state directory has no credential yet.
  if ! grep -qi '"apikey"' "$STATE/config.json" 2>/dev/null; then
    echo "== registering '$MACHINE' against $SERVER_URL =="
    "$BIN" set-server --url "$SERVER_URL" >/dev/null || die "set-server failed"
    "$BIN" register --name "$MACHINE" | head -2
  fi

  echo "== daemon on :$PORT =="
  cd "$PREFIX" || exit 1
  # PLAIN setsid+disown does NOT survive this SSH command returning: logind's KillUserProcesses
  # tracks by CGROUP, not POSIX session ID, and setsid's new session does not move the process out
  # of the SSH login session's cgroup - the daemon answers the check below just fine and is then
  # gone the moment this script exits. systemd-run --user --scope hands it to the Deck's own
  # `user@<uid>.service` manager instead (already running from the local desktop login), which is
  # independent of this SSH session and survives it closing. Confirmed on hardware 2026-08-19: `up`
  # reported success and the process was gone on the very next `status`, with the daemon's own log
  # showing it started and answered fine - it was killed from outside, not a startup failure.
  systemd-run --user --scope --unit="$SCOPE_UNIT" -- \
    "$BIN" daemon --port "$PORT" > "$DAEMON_LOG" 2>&1 < /dev/null &
  disown

  # Re-read the token EVERY iteration, not once before the loop - see testenv.sh's cmd_up for why
  # (a race with the freshly-backgrounded daemon still writing api-token, plus why the group-level
  # 2>/dev/null is needed rather than one on `tr` alone).
  local token out
  for _ in $(seq 1 40); do
    token=$( { tr -d '\r\n' < "$STATE/api-token"; } 2>/dev/null )
    out=$(curl -sf -m 3 -H "X-SaveLocker-Token: $token" "http://localhost:$PORT/api/state" 2>/dev/null)
    [ -n "$out" ] && break
    sleep 0.7
  done
  if [ -z "${out:-}" ]; then
    tail -5 "$DAEMON_LOG"
    die "daemon did not answer on :$PORT"
  fi
  echo "$out"
  show_real_game_mappings
}

cmd_down() {
  # Capture pids BEFORE stopping anything, so the report below reflects what was actually running
  # rather than what's left after the systemctl stop below already reaped it.
  local pids
  pids=$(pgrep -f "$DAEMON_PATTERN" | tr '\n' ' ')

  # Stop the scope directly rather than relying only on the pkill below: a transient scope is
  # removed once its main process exits, so killing the process SHOULD take the scope with it, but
  # doing this first means a leftover scope (the process having died some other way) can never make
  # the next `up` fail with "Unit savelocker-testenv-deck.scope already exists".
  systemctl --user stop "${SCOPE_UNIT}.scope" >/dev/null 2>&1 || true

  if [ -z "$pids" ]; then
    echo "no test daemon running"
    return 0
  fi
  # A headless daemon has no tray and no window, so this is the only way to close it. Harmless if
  # the systemctl stop above already did it - pkill just finds nothing left to kill.
  pkill -f "$DAEMON_PATTERN" 2>/dev/null
  sleep 1
  pkill -9 -f "$DAEMON_PATTERN" 2>/dev/null
  echo "stopped daemon (pid $pids)"
}

cmd_status() {
  local pids
  pids=$(pgrep -f "$DAEMON_PATTERN" | tr '\n' ' ')
  echo "daemon pids: ${pids:-none}"
  echo "prefix:      $PREFIX"
  echo "state dir:   $XDG_DATA_HOME"
  if [ -n "$pids" ]; then
    local token out
    token=$( { tr -d '\r\n' < "$STATE/api-token"; } 2>/dev/null )
    out=$(curl -sf -m 3 -H "X-SaveLocker-Token: $token" "http://localhost:$PORT/api/state" 2>/dev/null)
    echo "state:       ${out:-<no answer on :$PORT>}"
  fi
  show_real_game_mappings
}

cmd_logs() {
  if [ -f "$DAEMON_LOG" ]; then tail -20 "$DAEMON_LOG"; else echo "(no log yet)"; fi
}

cmd_clean() {
  cmd_down >/dev/null 2>&1
  rm -f "$TARBALL"
  if [ -d "$PREFIX" ]; then rm -rf "$PREFIX"; echo "removed $PREFIX"; fi
  if [ -d "$XDG_DATA_HOME" ]; then rm -rf "$XDG_DATA_HOME"; echo "removed $XDG_DATA_HOME"; fi
  rm -f "$DAEMON_LOG"
}

case "$CMD" in
  install) cmd_install ;;
  up)      cmd_up ;;
  down)    cmd_down ;;
  status)  cmd_status ;;
  logs)    cmd_logs ;;
  clean)   cmd_clean ;;
  *)       die "unknown command '$CMD'" ;;
esac
