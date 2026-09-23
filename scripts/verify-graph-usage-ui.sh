#!/usr/bin/env bash
# Verifies that MightyGraphView and AgentTranscriptFormat call ModelUsageFormat.
# Prints UI_WIRED_OK on success; exits non-zero on failure.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
FAILURES=0

check() {
  local label="$1"; local file="$2"; local pattern="$3"
  if ! grep -qF "$pattern" "$ROOT/$file"; then
    echo "FAIL: '$pattern' not found in $file" >&2
    FAILURES=$((FAILURES + 1))
  fi
}

# MightyGraphView must call blockCapsule and blockCapsuleHelp.
check "blockCapsule call"     native/macos/Sources/MightyClaude/MightyGraphView.swift     "ModelUsageFormat.blockCapsule"
check "blockCapsuleHelp call" native/macos/Sources/MightyClaude/MightyGraphView.swift     "ModelUsageFormat.blockCapsuleHelp"
check "records param"         native/macos/Sources/MightyClaude/MightyGraphView.swift     "records: records"
check "nodeModelLabel param"  native/macos/Sources/MightyClaude/MightyGraphView.swift     "nodeModelLabel: run.nodeModelLabel"
check "childBlocks param"     native/macos/Sources/MightyClaude/MightyGraphView.swift     "childBlocks: runChildBlocks"
check "a11y id"               native/macos/Sources/MightyClaude/MightyGraphView.swift     "mighty-tokens-"

# AgentTranscriptFormat must call activitySuffix.
check "activitySuffix call"   native/macos/Sources/MightyClaude/AgentTranscriptFormat.swift "ModelUsageFormat.activitySuffix"

# AgentTranscriptView must pass records and childBlocks to entry().
check "records update"        native/macos/Sources/MightyClaude/AgentTranscriptView.swift  "records: records"
check "childBlocks update"    native/macos/Sources/MightyClaude/AgentTranscriptView.swift  "childBlocks: childBlocks"

if [ "$FAILURES" -gt 0 ]; then
  echo "$FAILURES check(s) failed" >&2
  exit 1
fi
echo "UI_WIRED_OK"
