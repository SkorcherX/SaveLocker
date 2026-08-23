#!/usr/bin/env bash
# Linux half of tests/testenv.ps1 — driven from Windows, never run by hand in normal use.
#
# Everything here operates on the WSL clone (ext4), never /mnt: DrvFs breaks inotify, permissions,
# case-sensitivity and locking, and the suites depend on all four.
set -uo pipefail

CMD="${1:-status}"

REPO="${SAVELOCKER_WSL_REPO:-$HOME/SaveLocker}"
PORT="${SAVELOCKER_LINUX_PORT:-5187}"
SERVER_URL="${SAVELOCKER_SERVER_URL:-http://localhost:5080}"
VERSION="${SAVELOCKER_TEST_VERSION:-0.5.10-test}"
MACHINE="${SAVELOCKER_LINUX_MACHINE:-LinuxTest}"

# The whole isolation mechanism on Linux: this moves config.json, api-token, agent.log, the manifest
# cache and the lock directory together, so nothing here can touch a real install's state.
export XDG_DATA_HOME="${SAVELOCKER_LINUX_STATE:-$HOME/savelocker-test}"
STATE="$XDG_DATA_HOME/SaveLocker"
DLL="$REPO/src/Agent.Linux/bin/Debug/net10.0/savelocker.dll"
DAEMON_LOG="$HOME/.savelocker-testenv-daemon.log"
SUITE_LOG="$HOME/.savelocker-testenv-suite.log"

# A login shell's PATH is what puts ~/.dotnet on it, and this is not one.
if [ -x "$HOME/.dotnet/dotnet" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$HOME/.local/bin:$PATH"
fi

die() { echo "ERROR: $*" >&2; exit 1; }

# Only ever matches a dev build run as `dotnet savelocker.dll daemon`. A real install is a
# self-contained apphost named `savelocker`, so this pattern cannot reach one.
DAEMON_PATTERN='savelocker\.dll daemon'

# Without this the daemon serves 404 at / and the agent UI simply does not exist on Linux: the
# csproj copies agent-ui/dist next to the binary only if it is there, and WSL cannot build it —
# it has no native node, and the Windows one it finds on PATH fails on a UNC path. So the dist is
# built on Windows and staged here, which is the same thing build-linux.sh does for a release
# under SAVELOCKER_SKIP_UI_BUILD.
stage_ui() {
  local wt="${SAVELOCKER_WIN_REPO:-}"
  [ -n "$wt" ] || return 0
  [ -f "$wt/agent-ui/dist/index.html" ] || die "no agent-ui/dist on the Windows side - build it first"

  mkdir -p "$REPO/agent-ui"
  # cp -r into an existing directory copies INSIDE it rather than over it.
  rm -rf "$REPO/agent-ui/dist"
  # A plain copy, never the CR-stripping one used for sources: dist carries PNGs.
  cp -r "$wt/agent-ui/dist" "$REPO/agent-ui/dist" || die "staging agent-ui/dist failed"
  echo "staged agent-ui/dist ($(find "$REPO/agent-ui/dist" -type f | wc -l) files)"
}

cmd_build() {
  [ -d "$REPO" ] || die "no clone at $REPO"
  cd "$REPO" || exit 1
  stage_ui
  echo "== building the Linux agent as $VERSION =="
  # -p:Version is the right lever here: Agent.Linux.csproj carries no MinVer reference, so nothing
  # overwrites it. (The Windows agent is the opposite — see testenv.ps1.)
  dotnet build src/Agent.Linux/SaveLocker.Agent.Linux.csproj --no-incremental \
    -p:Version="$VERSION" -v q --nologo || die "build failed"
  echo "built: $(dotnet "$DLL" version 2>/dev/null)"
}

# The Deck target needs a SELF-CONTAINED linux-x64 publish — SteamOS ships no .NET runtime, so the
# framework-dependent `dotnet build` above (which needs the WSL clone's own dotnet on the far end)
# cannot run there at all. This reuses the real release packer, exactly as a tagged release would,
# just stamped with a test version instead. WSL is still where this runs, not Windows directly: it
# is the one place on this rig with both a Linux dotnet and agent-ui/dist already staged.
cmd_build_deck() {
  [ -d "$REPO" ] || die "no clone at $REPO"
  cd "$REPO" || exit 1
  stage_ui
  local dest="${SAVELOCKER_ARTIFACT_DIR:-}"
  [ -n "$dest" ] || die "SAVELOCKER_ARTIFACT_DIR not set"
  echo "== building the Deck agent as $VERSION (self-contained linux-x64) =="
  SAVELOCKER_SKIP_UI_BUILD=1 bash packaging/linux/build-linux.sh "$VERSION" linux-x64 || die "build failed"
  local tarball="$REPO/artifacts/linux/savelocker-${VERSION}-linux-x64.tar.gz"
  [ -f "$tarball" ] || die "expected tarball missing: $tarball"
  mkdir -p "$dest"
  cp "$tarball" "$dest/" || die "copy to $dest failed"
  echo "artifact: $dest/$(basename "$tarball")"
}

cmd_up() {
  [ -f "$DLL" ] || die "not built — run: testenv.ps1 build"
  cmd_down >/dev/null 2>&1

  if ! curl -sf -m 5 "$SERVER_URL/api/admin/status" >/dev/null 2>&1; then
    die "no test server at $SERVER_URL — start the console first"
  fi

  # Registering is idempotent from the caller's point of view but rotates the key, so only do it
  # when this state directory has no credential yet.
  # Case-insensitive: AgentConfig serialises with no naming policy, so the key on disk is "ApiKey".
  if ! grep -qi '"apikey"' "$STATE/config.json" 2>/dev/null; then
    echo "== registering '$MACHINE' against $SERVER_URL =="
    dotnet "$DLL" set-server --url "$SERVER_URL" >/dev/null || die "set-server failed"
    dotnet "$DLL" register --name "$MACHINE" | head -2
  fi

  echo "== daemon on :$PORT =="
  setsid dotnet "$DLL" daemon --port "$PORT" > "$DAEMON_LOG" 2>&1 < /dev/null &
  disown

  # Re-read the token EVERY iteration, not once before the loop: a freshly (re)started daemon can
  # still be writing api-token in the instant right after it's backgrounded, and reading it exactly
  # once there races that write - "No such file or directory" even though the daemon comes up fine
  # a moment later. Cost a debugging session on 2026-08-19 chasing a phantom "up" failure.
  # The 2>/dev/null on `tr` alone does NOT catch this: a failed `<` redirect is the shell's own
  # error, printed before `tr` ever runs, so it needs the whole group's stderr redirected instead -
  # confirmed by reproducing both ways.
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

  # The API answering does not mean the UI is there - it is a separate directory beside the binary,
  # and its absence shows up only as a 404 on a page nobody loaded yet.
  local ui
  ui=$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:$PORT/" 2>/dev/null)
  if [ "$ui" = "200" ]; then
    echo "UI: http://localhost:$PORT/ (reachable from Windows - WSL2 forwards it)"
  else
    echo "WARNING: the UI returns HTTP $ui - rebuild so agent-ui/dist gets staged" >&2
  fi
}

cmd_down() {
  local pids
  pids=$(pgrep -f "$DAEMON_PATTERN" | tr '\n' ' ')
  if [ -z "$pids" ]; then
    echo "no test daemon running"
    return 0
  fi
  # A headless daemon has no tray and no window, so this is the only way to close it.
  pkill -f "$DAEMON_PATTERN"
  sleep 1
  pkill -9 -f "$DAEMON_PATTERN" 2>/dev/null
  echo "stopped daemon (pid $pids)"
}

cmd_status() {
  local pids token out
  pids=$(pgrep -f "$DAEMON_PATTERN" | tr '\n' ' ')
  echo "daemon pids: ${pids:-none}"
  echo "state dir:   $STATE"
  if [ -n "$pids" ]; then
    token=$( { tr -d '\r\n' < "$STATE/api-token"; } 2>/dev/null )
    out=$(curl -sf -m 3 -H "X-SaveLocker-Token: $token" "http://localhost:$PORT/api/state" 2>/dev/null)
    echo "state:       ${out:-<no answer on :$PORT>}"
  fi
  if pgrep -f run-linux-tests >/dev/null 2>&1; then
    echo "suite:       RUNNING ($SUITE_LOG)"
  fi
}

# down only stops the daemon; this deletes what it left behind — the whole point being that
# `testenv.ps1 clean` can reclaim the disk without also wiping the WSL clone's normal dev state
# (the built DLLs live in the shared $REPO tree and are cheap to rebuild, so they are left alone).
cmd_clean() {
  cmd_down >/dev/null 2>&1
  if [ -d "$XDG_DATA_HOME" ]; then
    rm -rf "$XDG_DATA_HOME"
    echo "removed $XDG_DATA_HOME"
  else
    echo "nothing to remove ($XDG_DATA_HOME doesn't exist)"
  fi
}

cmd_test() {
  [ -d "$REPO" ] || die "no clone at $REPO"
  # The suite is sensitive to a stray agent — it scans for running games — so take the daemon down
  # first and put it back afterwards. Leaving the rig half up is worse than either state.
  local was_up=""
  pgrep -f "$DAEMON_PATTERN" >/dev/null 2>&1 && was_up=1
  cmd_down >/dev/null 2>&1

  cd "$REPO" || exit 1
  echo "== run-linux-tests.sh =="
  bash tests/linux/run-linux-tests.sh 2>&1 | tee "$SUITE_LOG" | tail -25
  local rc=${PIPESTATUS[0]}

  if [ -n "$was_up" ]; then
    # The suite builds the agent itself, with no version argument, so the rig would come back
    # reporting .NET's default 1.0.0 and the label would stop naming the build under test.
    echo "== restoring the daemon (re-stamping $VERSION, which the suite's own build cleared) =="
    cmd_build >/dev/null 2>&1
    cmd_up >/dev/null 2>&1 && echo "daemon back on :$PORT"
  fi
  echo "suite exit: $rc  (full log: $SUITE_LOG)"
  return $rc
}

# Brings the WSL clone to the commit under test, then lays the uncommitted changes on top. Both
# halves are needed: without the fetch the clone silently builds an OLDER tree than the one being
# tested - which has happened here before and surfaced only as a compile error, when it just as
# easily would not have.
#
# The file copy strips CR. Copying the tree wholesale is what must NOT happen: CRLF in
# tests/linux/*.sh fails six checks in ways that read as real wrapper bugs.
cmd_sync() {
  local wt="${SAVELOCKER_WIN_REPO:-}"
  [ -n "$wt" ] || die "SAVELOCKER_WIN_REPO not set"

  local gitdir="${SAVELOCKER_WIN_GITDIR:-}"
  local branch="${SAVELOCKER_BRANCH:-}"
  if [ -n "$gitdir" ] && [ -n "$branch" ]; then
    # Fetching a /mnt path otherwise fails with "dubious ownership".
    git config --global --get-all safe.directory 2>/dev/null | grep -qxF "$gitdir" ||
      git config --global --add safe.directory "$gitdir"

    echo "fetching $branch from $gitdir"
    git -C "$REPO" fetch --quiet "$gitdir" "$branch" || die "fetch failed"
    git -C "$REPO" checkout --quiet --force FETCH_HEAD || die "checkout failed"
    echo "  clone now at $(git -C "$REPO" log --oneline -1)"
  fi

  local n=0
  while IFS= read -r rel; do
    # The list is written by PowerShell, so every line arrives with a trailing CR - which turns a
    # real path into one that does not exist, reported as "skip (gone)".
    rel="${rel%$'\r'}"
    [ -n "$rel" ] || continue
    if [ ! -f "$wt/$rel" ]; then echo "  skip (gone): $rel"; continue; fi
    mkdir -p "$REPO/$(dirname "$rel")"
    sed 's/\r$//' "$wt/$rel" > "$REPO/$rel" || die "copy failed: $rel"
    echo "  $rel"
    n=$((n + 1))
  done
  echo "synced $n file(s) into $REPO"
}

case "$CMD" in
  build)       cmd_build ;;
  build-deck)  cmd_build_deck ;;
  up)          cmd_up ;;
  down)        cmd_down ;;
  status)      cmd_status ;;
  test)        cmd_test ;;
  sync)        cmd_sync ;;
  clean)       cmd_clean ;;
  *)           die "unknown command '$CMD'" ;;
esac
