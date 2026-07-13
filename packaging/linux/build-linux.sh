#!/usr/bin/env bash
# Build a self-contained SaveLocker agent tarball for Linux (Steam Deck / SteamOS).
#
# Self-contained is MANDATORY: SteamOS ships no .NET runtime.
#
# Build on the OLDEST glibc you intend to support. A self-contained .NET binary binds to the
# host glibc at build time; an older-glibc build runs on newer systems but never the reverse.
# Ubuntu 24.04 (glibc 2.39) is older than SteamOS's rolling Arch, so Ubuntu -> Deck is safe.
# Building on Arch and shipping to anything older produces "GLIBC_2.4x not found" on user hardware.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
out="${repo_root}/artifacts/linux"
rid="${1:-linux-x64}"

echo "==> Building agent UI (the daemon's only UI)"
if [ -d "${repo_root}/agent-ui" ]; then
  (cd "${repo_root}/agent-ui" && npm ci --silent && npm run build --silent)
fi

echo "==> Publishing savelocker (${rid}, self-contained)"
rm -rf "${out}"
dotnet publish "${repo_root}/src/Agent.Linux/SaveLocker.Agent.Linux.csproj" \
  -c Release \
  -r "${rid}" \
  --self-contained true \
  -o "${out}/SaveLocker"

cp "${repo_root}/packaging/linux/install.sh" "${out}/SaveLocker/"
cp "${repo_root}/packaging/linux/savelocker.service" "${out}/SaveLocker/"
chmod +x "${out}/SaveLocker/install.sh" "${out}/SaveLocker/savelocker"

tarball="${out}/savelocker-${rid}.tar.gz"
tar -czf "${tarball}" -C "${out}" SaveLocker

echo
echo "Built: ${tarball}"
echo "Install on the Deck:  tar -xzf $(basename "${tarball}") && ./SaveLocker/install.sh"
