#!/usr/bin/env bash
# Verifies that MightyCoreTests/ModelUsageTests passes with the naming contract.
# Prints CORE_MODEL_USAGE_OK on success; exits non-zero on failure.
set -euo pipefail
export DEVELOPER_DIR=/Library/Developer/CommandLineTools
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

# 1. All eight named test functions must exist in the source.
REQUIRED_TESTS=(
  perBlockModelsAndTokensFromStream
  activityLineShowsCallingResponse
  sharedResponseShowsNumbersOnFirstActivityOnly
  activityLinesSumToBlockTotal
  subagentActivityShowsChildBlockTotal
  capsuleShowsConfiguredModelBeforeFirstResponse
  codexResponsesUseRunModel
  resentResponseReplacesItsRecord
)
FAILURES=0
for fn in "${REQUIRED_TESTS[@]}"; do
  if ! grep -qF "func ${fn}()" "$ROOT/native/macos/Tests/MightyCoreTests/ModelUsageTests.swift"; then
    echo "FAIL: test function '${fn}' not found in ModelUsageTests.swift" >&2
    FAILURES=$((FAILURES + 1))
  fi
done
if [ "$FAILURES" -gt 0 ]; then
  echo "$FAILURES check(s) failed" >&2
  exit 1
fi

# 2. Run the test suite.
bash "$ROOT/scripts/test-native-macos.sh" --filter ModelUsageTests --scratch-path /tmp/mc-swift-v5-07

echo "CORE_MODEL_USAGE_OK"
