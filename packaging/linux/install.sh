#!/usr/bin/env bash
# Install the SaveLocker agent for the current user.
#
# Everything lands in $HOME. Nothing is written to /usr and nothing needs root: SteamOS's root
# filesystem is immutable and is wiped on every system update, so a system-wide install would
# silently disappear (Decisions.md §5).
set -euo pipefail

src="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
prefix="${HOME}/.local/share/SaveLocker"
bindir="${HOME}/.local/bin"
unitdir="${HOME}/.config/systemd/user"

echo "==> Installing to ${prefix}"
mkdir -p "${prefix}" "${bindir}" "${unitdir}"

# Upgrading over a RUNNING agent used to corrupt the install silently, so stop it first.
#
# Two separate failures, both invisible: Linux refuses to write to a file it is executing
# (`cp: Text file busy`), so the binary was never replaced; and the managed DLLs beside it ARE
# writable while mapped, so overwriting them killed the running daemon with SIGBUS. The script then
# printed "Installed." and exited 0, because `find -exec` does not report its child's exit status
# and `set -e` therefore never fired. Re-running install.sh from a newer tarball is THE documented
# Linux update path, so this hit every upgrade.
restart_after=0
if command -v systemctl >/dev/null 2>&1 && systemctl --user is-active --quiet savelocker.service 2>/dev/null; then
  echo "==> Stopping the running agent before replacing its files"
  systemctl --user stop savelocker.service 2>/dev/null || true
  restart_after=1
fi

# A daemon started by hand is not under systemd's control, and we must not kill something the user
# is running deliberately — but they need to know this install will not take effect until it restarts.
stray_daemon=0
if command -v pgrep >/dev/null 2>&1 && pgrep -f "savelocker daemon" >/dev/null 2>&1; then
  stray_daemon=1
fi

# --remove-destination unlinks each target before writing, so the replacement gets a NEW inode and
# anything still running keeps the old one. That is what makes the copy safe even if a process we
# could not stop is holding these files: it survives on the old inode instead of taking a SIGBUS.
copy_failed=0
while IFS= read -r -d '' item; do
  cp -r --remove-destination "${item}" "${prefix}/" || copy_failed=1
done < <(find "${src}" -maxdepth 1 -mindepth 1 ! -name install.sh ! -name savelocker.service -print0)

if [ "${copy_failed}" -ne 0 ]; then
  echo "!! Install FAILED: could not replace files in ${prefix}"
  echo "   Something is still using them. Stop the agent and re-run:"
  echo "       systemctl --user stop savelocker.service"
  exit 1
fi

chmod +x "${prefix}/savelocker"

ln -sf "${prefix}/savelocker" "${bindir}/savelocker"
echo "==> Linked ${bindir}/savelocker"

# Auto-start is a BONUS, never a reason to fail the install. The agent is already installed and
# usable by this point, so every failure below is a warning with a next step — not an abort.
#
# `systemctl` existing is not the same as it being usable: installing over SSH on a Deck (a very
# normal thing to do) gives you a systemctl binary and NO user bus, and `systemctl --user` then
# fails with "Failed to connect to bus". Under `set -e` that used to kill the script AFTER the
# binary was in place, leaving the user with an error and no idea they were actually fine.
install_unit() {
  command -v systemctl >/dev/null 2>&1 || {
    echo "!! systemd not found — skipping the auto-start unit."
    return 1
  }

  install -m 0644 "${src}/savelocker.service" "${unitdir}/savelocker.service"

  systemctl --user daemon-reload >/dev/null 2>&1 || {
    echo "!! systemd --user is not usable in this session (no user bus — common over SSH)."
    echo "   The unit IS installed at ${unitdir}/savelocker.service."
    echo "   Enable it from a desktop session with:  systemctl --user enable --now savelocker.service"
    return 1
  }

  systemctl --user enable --now savelocker.service >/dev/null 2>&1 || {
    echo "!! could not enable savelocker.service automatically."
    echo "   Try:  systemctl --user enable --now savelocker.service"
    return 1
  }

  echo "==> systemd --user unit enabled (savelocker.service) — the agent is running"

  # Without lingering, a --user unit stops when the user logs out. On a Deck that is usually
  # fine (you are logged in whenever you play), so this is a hint, not a failure.
  if command -v loginctl >/dev/null 2>&1 && \
     [ "$(loginctl show-user "$USER" -p Linger --value 2>/dev/null || echo no)" != "yes" ]; then
    echo "   (to keep it running while logged out: sudo loginctl enable-linger $USER)"
  fi
}

if ! install_unit; then
  echo "   You can always run the agent directly:  ${prefix}/savelocker daemon"
  # We stopped a working agent for the upgrade and could not bring it back. Say so loudly: on a
  # headless Deck the only symptom otherwise is the machine quietly going offline in the console.
  if [ "${restart_after}" -eq 1 ]; then
    echo "!! The agent was RUNNING before this upgrade and is now STOPPED."
    echo "   Start it from a desktop session with:  systemctl --user start savelocker.service"
  fi
fi

if [ "${stray_daemon}" -eq 1 ]; then
  echo "!! A 'savelocker daemon' you started by hand is still running the PREVIOUS version."
  echo "   It was left alone on purpose. Restart it to pick up this one."
fi

if ! echo "${PATH}" | tr ':' '\n' | grep -qx "${bindir}"; then
  echo "!! ${bindir} is not on your PATH — add it, or use the full path ${prefix}/savelocker"
fi

cat <<'EOF'

Installed. Next:

  1. In the SaveLocker console: Configuration -> "Enroll a machine" -> Create
     enrollment file. Copy the downloaded .json to this device, then:

         savelocker enroll --file savelocker-enroll-*.json

     The file carries a single-use token that expires in ~15 minutes, so no API
     key is ever copied by hand. It sets the server URL, registers this machine,
     and picks up the games already defined on the server.

     (No console access? The manual path still works:
        savelocker set-server --url https://your-server
        savelocker register --name "Steam Deck")

  2. savelocker doctor            # checks the whole chain -- start here if anything is off

  3. Add this to a game's Steam launch options:

         ${HOME}/.local/bin/savelocker run -- %command%

     Use the full path above. Game Mode does not put ~/.local/bin on PATH, so
     the short form 'savelocker run -- %command%' silently prevents the game
     from launching.

     (For a non-Steam shortcut, tick "Force the use of a specific Steam Play
      compatibility tool" in its properties so Proton sets up a prefix. Without
      it, Proton never creates a prefix and there is nothing to sync.)

  The agent runs headless -- there is no tray and no pop-ups on a Deck. It
  reports problems to the console, which is where you will see them.
EOF
