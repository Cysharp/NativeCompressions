#!/usr/bin/env bash
# Run from the SDK-pinned validation directory. Read-only; installs nothing.
set -euo pipefail

if [[ "$(uname -s)" != Darwin ]]; then
  echo 'This probe requires macOS.' >&2
  exit 1
fi

for command in dotnet xcodebuild xcrun sw_vers; do
  command -v "$command" >/dev/null
done

printf '\n## Collected at (UTC)\n'
date -u '+%Y-%m-%dT%H:%M:%SZ'
printf '\n## Host\n'
sw_vers
uname -m
printf '\n## Selected .NET SDK\n'
dotnet --version
dotnet --info
dotnet --list-sdks
printf '\n## Workloads\n'
dotnet workload list
dotnet workload --info
printf '\n## Xcode selection\n'
printf 'DEVELOPER_DIR=%s\n' "${DEVELOPER_DIR:-<unset>}"
xcode-select -p
xcodebuild -version
printf '\n## Apple SDK\n'
xcrun --sdk macosx --show-sdk-path
xcrun --sdk macosx --show-sdk-version
xcrun --sdk macosx clang --version
printf '\nCollection complete; compare versions with mac-catalyst-phase0.md.\n'
