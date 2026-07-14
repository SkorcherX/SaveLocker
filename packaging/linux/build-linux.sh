#!/usr/bin/env bash
# Build a self-contained SaveLocker agent tarball for Linux (Steam Deck / SteamOS).
#
# Self-contained is MANDATORY: SteamOS ships no .NET runtime.
#
# Build on the OLDEST glibc you intend to support. A self-contained .NET binary binds to the
# host glibc at build time; an older-glibc build runs on newer systems but never the reverse.
# Ubuntu 24.04 (glibc 2.39) is older than SteamOS's rolling Arch, so Ubuntu -> Deck is safe.
# Building on Arch and shipping to anything older produces "GLIBC_2.4x not found" on user hardware.
#
# Usage: build-linux.sh [version] [rid]
#   build-linux.sh 0.2.0            -> artifacts/linux/savelocker-0.2.0-linux-x64.tar.gz
#   build-linux.sh                  -> version 0.0.0-dev (a local build, clearly labelled as one)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
out="${repo_root}/artifacts/linux"
version="${1:-0.0.0-dev}"
rid="${2:-linux-x64}"

# The numeric part only: AssemblyVersion/FileVersion reject a "-rc1" suffix.
numeric_version="${version%%-*}"

echo "==> Building agent UI (the daemon's only UI)"
if [ -d "${repo_root}/agent-ui" ]; then
  (cd "${repo_root}/agent-ui" && npm ci --silent && npm run build --silent)
fi

echo "==> Publishing savelocker ${version} (${rid}, self-contained)"
rm -rf "${out}"

# The version is passed EXPLICITLY. MinVer silently stamps 0.0.0.0 when it cannot reach the git
# history (which is the norm on CI runners — see Gotchas.md), and the agent reports its version to
# the console in every heartbeat, so an unstamped build shows up in the dashboard as a lie.
dotnet publish "${repo_root}/src/Agent.Linux/SaveLocker.Agent.Linux.csproj" \
  -c Release \
  -r "${rid}" \
  --self-contained true \
  -o "${out}/SaveLocker" \
  "--property:Version=${numeric_version}" \
  "--property:AssemblyVersion=${numeric_version}" \
  "--property:FileVersion=${numeric_version}" \
  "--property:InformationalVersion=${version}"

cp "${repo_root}/packaging/linux/install.sh" "${out}/SaveLocker/"
cp "${repo_root}/packaging/linux/savelocker.service" "${out}/SaveLocker/"
chmod +x "${out}/SaveLocker/install.sh" "${out}/SaveLocker/savelocker"

tarball="${out}/savelocker-${version}-${rid}.tar.gz"
tar -czf "${tarball}" -C "${out}" SaveLocker

echo
echo "Built: ${tarball}"
echo "Install on the Deck:  tar -xzf $(basename "${tarball}") && ./SaveLocker/install.sh"
