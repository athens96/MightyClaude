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

LOGFILE="$(mktemp /tmp/swift-test-XXXXXX.log)"
set +e
"$SWIFT_EXECUTABLE" test "${TEST_ARGUMENTS[@]}" "$@" 2>&1 | tee "$LOGFILE"
SWIFT_EXIT=${PIPESTATUS[0]}
set -e

if [ "$SWIFT_EXIT" -ne 0 ]; then
  python3 - "$LOGFILE" <<'PY'
import sys
all_lines = open(sys.argv[1]).read().splitlines()
nonempty = [l for l in all_lines if l.strip()]
# Compiler errors (": error:"), test failures ("✗"), and the final summary
errors = [l for l in nonempty if ': error:' in l][:8]
failures = [l for l in nonempty if l.strip().startswith('✗')][:8]
summary = [l for l in nonempty if 'Test run with' in l or 'test run with' in l][:2]
tail = nonempty[-10:]
seen = set(); result = []
for l in errors + failures + summary + tail:
    if l not in seen:
        result.append(l); seen.add(l)
msg = '\n'.join(result[:30])
msg = msg.replace('%', '%25').replace('\r', '%0D').replace('\n', '%0A')[:1500]
print(f'::error title=macOS Swift tests::{msg}')
PY
  rm -f "$LOGFILE"
  exit "$SWIFT_EXIT"
fi
rm -f "$LOGFILE"
