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

LOGFILE="$(mktemp /tmp/swift-test.XXXXXX)"
set +e
"$SWIFT_EXECUTABLE" test "${TEST_ARGUMENTS[@]}" "$@" 2>&1 | tee "$LOGFILE"
SWIFT_EXIT=${PIPESTATUS[0]}
set -e

if [ "$SWIFT_EXIT" -ne 0 ]; then
  MIGHTY_PROJECT_ROOT="$PROJECT_ROOT" python3 - "$LOGFILE" <<'PY'
import os, re, sys

root = os.environ.get("MIGHTY_PROJECT_ROOT", "")
# Nothing outside the checkout may reach a public annotation: the checkout prefix
# becomes a repo-relative path and every other absolute path is redacted.
OUTSIDE = re.compile(r'/(?:Users|home|private|var|tmp|opt|Applications|Library)/[^\s:,\'")\]]*')

def clean(line):
    if root:
        line = line.replace(root + "/", "").replace(root, "")
    line = OUTSIDE.sub("<path>", line)
    return line[:240]

nonempty = [clean(l) for l in open(sys.argv[1], errors="replace").read().splitlines() if l.strip()]
errors = [l for l in nonempty if ': error:' in l][:12]
failures = [l for l in nonempty if l.strip().startswith('✗')][:8]
# The contract: the last 30 non-empty lines of a failing run, with the compiler
# errors and the failing checks hoisted in front of them.
tail = nonempty[-30:]
seen, selected = set(), []
for l in errors + failures + tail:
    if l not in seen:
        selected.append(l)
        seen.add(l)

def encode(text):
    return text.replace('%', '%25').replace('\r', '%0D').replace('\n', '%0A')

# Annotations are capped at 1500 characters, so the selection is emitted as a
# short series of capped annotations rather than silently truncated to one.
chunk, chunks = [], []
for line in selected:
    candidate = chunk + [line]
    if len(encode('\n'.join(candidate))) > 1500 and chunk:
        chunks.append(chunk)
        chunk = [line]
    else:
        chunk = candidate
if chunk:
    chunks.append(chunk)

for index, part in enumerate(chunks[:3], 1):
    suffix = f' ({index}/{min(len(chunks), 3)})' if len(chunks) > 1 else ''
    print(f'::error title=macOS Swift tests{suffix}::{encode(chr(10).join(part))[:1500]}')
PY
  rm -f "$LOGFILE"
  exit "$SWIFT_EXIT"
fi
rm -f "$LOGFILE"
