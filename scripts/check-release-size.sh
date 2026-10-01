#!/bin/bash
# Release size gate for the packages the Mac self-update downloads.
#
# The app refuses any package above 512 MiB (MightyCore AppUpdate.maximumPackageBytes),
# so a release that grows past that is unusable: existing installs can never update to
# it. The gate here stops well short of that cliff at 480 MiB, which leaves room for the
# bundled browser engine and the WebRTC framework to grow between releases without the
# next build suddenly becoming un-installable.
#
# Usage:
#   scripts/check-release-size.sh [package.zip ...]
#
# With no argument it checks release/MightyClaude-macos.zip. Run it after packaging and
# before uploading anything to the release bucket (see docs/app-update.md).
set -euo pipefail

GATE_MIB=480
UPDATER_CAP_MIB=512
MIB=1048576
GATE_BYTES=$((GATE_MIB * MIB))

PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

file_bytes() {
  # Apparent size, which is what gets uploaded and downloaded; a sparse file still
  # costs its full length over the wire.
  if [ "$(uname -s)" = "Darwin" ]; then
    stat -f%z "$1"
  else
    stat -c%s "$1"
  fi
}

human_mib() {
  awk -v b="$1" -v m="$MIB" 'BEGIN { printf "%.1f", b / m }'
}

packages=("$@")
if [ "${#packages[@]}" -eq 0 ]; then
  packages=("$PROJECT_ROOT/release/MightyClaude-macos.zip")
fi

failed=0
for path in "${packages[@]}"; do
  if [ ! -f "$path" ]; then
    echo "FAIL: $path does not exist, so its size cannot be gated" >&2
    failed=1
    continue
  fi
  bytes="$(file_bytes "$path")"
  size="$(human_mib "$bytes")"
  if [ "$bytes" -gt "$GATE_BYTES" ]; then
    echo "FAIL: $path is $size MiB, above the $GATE_MIB MiB release gate (updater cap $UPDATER_CAP_MIB MiB)." >&2
    echo "      Shrink the package before upload; installs on the old version cannot update to it." >&2
    failed=1
  else
    echo "OK: $path is $size MiB, within the $GATE_MIB MiB release gate (updater cap $UPDATER_CAP_MIB MiB)"
  fi
done

if [ "$failed" -ne 0 ]; then
  exit 1
fi

echo "PASS: every release package is under the $GATE_MIB MiB gate"
