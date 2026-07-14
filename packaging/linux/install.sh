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

# Copy everything except this script and the unit template.
find "${src}" -maxdepth 1 -mindepth 1 \
  ! -name install.sh ! -name savelocker.service \
  -exec cp -r {} "${prefix}/" \;
chmod +x "${prefix}/savelocker"

ln -sf "${prefix}/savelocker" "${bindir}/savelocker"
echo "==> Linked ${bindir}/savelocker"

if ! command -v systemctl >/dev/null 2>&1; then
  echo "!! systemd not found — skipping the auto-start unit."
  echo "   Start the agent by hand with: ${prefix}/savelocker daemon"
else
  install -m 0644 "${src}/savelocker.service" "${unitdir}/savelocker.service"
  systemctl --user daemon-reload
  systemctl --user enable --now savelocker.service
  echo "==> systemd --user unit enabled (savelocker.service)"

  # Without lingering, a --user unit stops when the user logs out. On a Deck that is usually
  # fine (you are logged in whenever you play), so this is a hint, not a failure.
  if command -v loginctl >/dev/null 2>&1 && \
     [ "$(loginctl show-user "$USER" -p Linger --value 2>/dev/null || echo no)" != "yes" ]; then
    echo "   (to keep it running while logged out: sudo loginctl enable-linger $USER)"
  fi
fi

if ! echo "${PATH}" | tr ':' '\n' | grep -qx "${bindir}"; then
  echo "!! ${bindir} is not on your PATH — add it, or use the full path ${prefix}/savelocker"
fi

cat <<'EOF'

Installed. Next:

  1. savelocker set-server --url https://your-server
  2. savelocker register --name "Steam Deck"
  3. savelocker doctor            # checks the whole chain
  4. Add this to a game's Steam launch options:

         savelocker run -- %command%

     (For a non-Steam shortcut, tick "Force the use of a specific Steam Play
      compatibility tool" in its properties so Proton sets up a prefix.)
EOF
