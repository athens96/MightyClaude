#!/bin/bash
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# Respect an explicit DEVELOPER_DIR, otherwise use xcode-select (including CI's selected Xcode).
# Never replace the selected toolchain with a hard-coded Command Line Tools path.
SWIFT_EXECUTABLE="$(xcrun --find swift)"
TESTING_PLUGIN="$(dirname "$SWIFT_EXECUTABLE")/../lib/swift/host/plugins/testing/libTestingMacros.dylib"
TEST_ARGUMENTS=(--package-path "$PROJECT_ROOT/native/macos" --disable-xctest --enable-swift-testing)
if [[ -f "$TESTING_PLUGIN" ]]; then
  TEST_ARGUMENTS+=(-Xswiftc -load-plugin-library -Xswiftc "$TESTING_PLUGIN")
fi
"$SWIFT_EXECUTABLE" test "${TEST_ARGUMENTS[@]}" "$@"
