#!/bin/bash
# F-02 regression: verify build-macos.sh does not set NSAllowsArbitraryLoads
# globally. Extracts the inline Info.plist heredoc and checks with plutil.
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SCRIPT="$PROJECT_ROOT/scripts/build-macos.sh"

# Extract the inline plist between <<'PLIST' ... PLIST
PLIST_TEXT="$(awk "/<<'PLIST'/{found=1; next} /^PLIST$/{found=0} found" "$SCRIPT")"

if [ -z "$PLIST_TEXT" ]; then
  echo "FAIL: could not extract inline plist from build-macos.sh" >&2
  exit 1
fi

TMPFILE="$(mktemp /tmp/mighty-ats-test.XXXXXX.plist)"
printf '%s\n' "$PLIST_TEXT" > "$TMPFILE"

# Any NSAllowsArbitraryLoads value under NSAppTransportSecurity is a failure.
if plutil -extract NSAppTransportSecurity.NSAllowsArbitraryLoads raw "$TMPFILE" > /dev/null 2>&1; then
  echo "FAIL: NSAllowsArbitraryLoads is still set in build-macos.sh" >&2
  rm -f "$TMPFILE"
  exit 1
fi

# Local networking (Ouroboros dashboard, ModBridge, LAN relay) must stay allowed.
if [ "$(plutil -extract NSAppTransportSecurity.NSAllowsLocalNetworking raw "$TMPFILE" 2>/dev/null)" != "true" ]; then
  echo "FAIL: NSAllowsLocalNetworking is missing from build-macos.sh" >&2
  rm -f "$TMPFILE"
  exit 1
fi

# Also verify it is syntactically valid XML plist
if ! plutil -lint "$TMPFILE" > /dev/null 2>&1; then
  echo "FAIL: inline plist in build-macos.sh is not valid XML" >&2
  rm -f "$TMPFILE"
  exit 1
fi

rm -f "$TMPFILE"
echo "PASS: NSAllowsArbitraryLoads is absent from build-macos.sh plist (F-02)"
