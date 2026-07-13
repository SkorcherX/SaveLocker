#!/usr/bin/env bash
# A game that keeps writing its save AFTER its process has exited.
#
# This is the case the settle gate exists for, and it is not hypothetical: many games flush for
# several seconds after the window closes. If the agent archived on the exit event alone it would
# capture a half-written save and publish it as a version.
#
# So: spawn a detached writer that outlives us, then exit immediately. From the launch wrapper's
# point of view the game is gone — but the save is still being written.
#
# Modes:
#   write (default) — writes a line a second for <seconds>, so BOTH halves of the gate see it.
#   hold            — writes once, then sits perfectly still while still holding the file open
#                     for writing. The fingerprint goes quiet immediately, so ONLY the /proc
#                     lock probe can keep the gate closed. This is the mode that proves the probe
#                     actually detects a writer, rather than merely reporting itself "available".
#
# Usage: slow-game.sh <save-dir> <seconds> <exit-code> [write|hold]
set -euo pipefail

save_dir="$1"
seconds="${2:-6}"
exit_code="${3:-0}"
mode="${4:-write}"

mkdir -p "${save_dir}"

setsid bash -c '
  save_dir="$1"
  seconds="$2"
  mode="$3"

  # Hold the file open for WRITING for the whole run. On Linux this is what the /proc/*/fd probe
  # must detect — FileShare is not enforced by the kernel, so a naive Windows-style lock check
  # sees nothing here and would wrongly report the directory quiet.
  exec 3> "${save_dir}/slot1.sav"

  if [ "${mode}" = "hold" ]; then
    # One write, then silence — but the descriptor stays open for writing the whole time.
    echo "holding" >&3
    sync
    sleep "${seconds}"
  else
    for i in $(seq 1 "${seconds}"); do
      echo "level=${i}" >&3
      # Force the bytes out so the file size actually changes each second (the fingerprint half
      # of the gate watches size + mtime).
      sync
      sleep 1
    done
  fi

  echo "done" >&3
  exec 3>&-
' _ "${save_dir}" "${seconds}" "${mode}" >/dev/null 2>&1 < /dev/null &

# The "game process" exits now. The writer above keeps going.
exit "${exit_code}"
