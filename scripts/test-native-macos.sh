#!/bin/bash
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

# Locally the unfiltered suite currently deadlocks (about 200 async tests, mostly
# ScreenShare*/Relay*, park at 0% CPU and never finish), which stalls automated
# workers until they are abandoned. Outside CI, require --filter unless the full
# run is asked for explicitly with MIGHTY_FULL_SUITE=1.
if [[ -z "${GITHUB_ACTIONS:-}" && "${MIGHTY_FULL_SUITE:-}" != "1" ]]; then
  has_filter=0
  for arg in "$@"; do
    case "$arg" in --filter|--filter=*) has_filter=1 ;; esac
  done
  if [[ "$has_filter" -eq 0 ]]; then
    echo "test-native-macos.sh: pass --filter \"SuiteA|SuiteB\" (the unfiltered suite deadlocks locally); set MIGHTY_FULL_SUITE=1 to run everything anyway." >&2
    exit 2
  fi
fi
# Respect an explicit DEVELOPER_DIR, otherwise use xcode-select (including CI's selected Xcode).
# Never replace the selected toolchain with a hard-coded Command Line Tools path.
SWIFT_EXECUTABLE="$(xcrun --find swift)"
TESTING_PLUGIN="$(dirname "$SWIFT_EXECUTABLE")/../lib/swift/host/plugins/testing/libTestingMacros.dylib"
TEST_ARGUMENTS=(--package-path "$PROJECT_ROOT/native/macos" --disable-xctest --enable-swift-testing)
if [[ -f "$TESTING_PLUGIN" ]]; then
  TEST_ARGUMENTS+=(-Xswiftc -load-plugin-library -Xswiftc "$TESTING_PLUGIN")
fi

LOGFILE="$(mktemp /tmp/swift-test.XXXXXX)"
# Swift Testing's event stream names every failing test with its suite, and
# shows a test that started but never ended. Asked for unless the caller
# already did, and only from a toolchain that knows the option.
EVENTS=""
caller_events=0
for arg in "$@"; do
  case "$arg" in --event-stream-output-path|--event-stream-output-path=*|--experimental-event-stream-output|--experimental-event-stream-output=*) caller_events=1 ;; esac
done
# Read whole rather than piped into `grep -q`, whose early exit can fail the pipe.
SWIFT_TEST_HELP="$("$SWIFT_EXECUTABLE" test --help-hidden 2>/dev/null || true)"
if [[ "$caller_events" -eq 0 && "$SWIFT_TEST_HELP" == *--event-stream-output-path* ]]; then
  EVENTS="$(mktemp /tmp/swift-test-events.XXXXXX)"
  TEST_ARGUMENTS+=(--event-stream-output-path "$EVENTS")
fi
set +e
"$SWIFT_EXECUTABLE" test "${TEST_ARGUMENTS[@]}" "$@" 2>&1 | tee "$LOGFILE"
SWIFT_EXIT=${PIPESTATUS[0]}
set -e

if [ "$SWIFT_EXIT" -ne 0 ]; then
  MIGHTY_PROJECT_ROOT="$PROJECT_ROOT" python3 - "$LOGFILE" "$EVENTS" <<'PY'
import json, os, re, sys

root = os.environ.get("MIGHTY_PROJECT_ROOT", "")
# Nothing outside the checkout may reach a public annotation: the checkout prefix
# becomes a repo-relative path and every other absolute path is redacted.
OUTSIDE = re.compile(r'/(?:Users|home|private|var|tmp|opt|Applications|Library)/[^\s:,\'")\]]*')

def clean(line, limit=240):
    if root:
        line = line.replace(root + "/", "").replace(root, "")
    line = OUTSIDE.sub("<path>", line)
    return line[:limit]

def encode(text):
    return text.replace('%', '%25').replace('\r', '%0D').replace('\n', '%0A')

def prop(text):
    # Workflow-command properties are split on ',' and ':', so a title escapes them too.
    return encode(text).replace(':', '%3A').replace(',', '%2C')

def chunked(lines, size=1500):
    """Groups lines into annotation bodies of at most `size` encoded characters."""
    chunk, chunks = [], []
    for line in lines:
        candidate = chunk + [line]
        if len(encode('\n'.join(candidate))) > size and chunk:
            chunks.append(chunk)
            chunk = [line]
        else:
            chunk = candidate
    if chunk:
        chunks.append(chunk)
    return chunks

nonempty = [clean(l) for l in open(sys.argv[1], errors="replace").read().splitlines() if l.strip()]

# Every failing test, compactly: "Suite/test() :line" from the event stream,
# or "test() File.swift:line" read off the console when there is none.
failing, unfinished = {}, []
events = sys.argv[2] if len(sys.argv) > 2 else ""
if events and os.path.exists(events):
    started, ended = [], set()
    for raw in open(events, errors="replace"):
        try:
            record = json.loads(raw)
        except ValueError:
            continue
        if record.get("kind") != "event":
            continue
        payload = record.get("payload", {})
        kind, test = payload.get("kind"), payload.get("testID") or ""
        if kind == "testStarted":
            started.append(test)
        elif kind in ("testEnded", "testSkipped"):
            ended.add(test)
        elif kind == "issueRecorded" and test:
            issue = payload.get("issue") or {}
            # Known issues and warnings do not fail a test.
            if issue.get("isKnown") or issue.get("severity") == "warning":
                continue
            # "Module.Suite/test()/File.swift:12:6" -> "Suite/test()"
            name = "/".join(test.split("/")[:-1]) if re.search(r'\.swift:\d+:\d+$', test) else test
            name = name.split(".", 1)[-1] if "." in name.split("/")[0] else name
            line = ((payload.get("issue") or {}).get("sourceLocation") or {}).get("line")
            failing.setdefault(clean(name, 160), []).append(str(line) if line else "?")
    for test in started:
        if test not in ended and re.search(r'\.swift:\d+:\d+$', test):
            name = "/".join(test.split("/")[:-1])
            unfinished.append(clean(name.split(".", 1)[-1] if "." in name.split("/")[0] else name, 160))
if not failing:
    for l in nonempty:
        m = re.match(r'^[✘✗] Test (.+?) recorded an issue at ([^\s:]+):(\d+)', l)
        if m:
            failing.setdefault(f"{m.group(1)} {m.group(2)}", []).append(m.group(3))

summary = [f"{name} :{','.join(dict.fromkeys(lines))}" for name, lines in failing.items()]
summary += [f"{name} (never finished)" for name in unfinished]
if summary:
    # Annotations are capped (1500 characters each, ten errors per step), so the
    # list gets up to six and the details below keep their three.
    parts = chunked(summary)
    if len(parts) > 6:
        shown = sum(len(p) for p in parts[:6])
        parts = parts[:6]
        tail = parts[-1]
        while tail:
            more = f"... and {len(summary) - shown + 1} more"
            if len(encode('\n'.join(tail[:-1] + [more]))) <= 1500 or len(tail) == 1:
                parts[-1] = tail[:-1] + [more]
                break
            tail, shown = tail[:-1], shown - 1
    title = f"macOS Swift tests: {len(failing)} failing" + (f", {len(unfinished)} never finished" if unfinished else "")
    for index, part in enumerate(parts, 1):
        suffix = f' ({index}/{len(parts)})' if len(parts) > 1 else ''
        print(f'::error title={prop(title + suffix)}::{encode(chr(10).join(part))[:1500]}')

errors = [l for l in nonempty if ': error:' in l][:12]
# Swift Testing marks a failure with U+2718; U+2717 is kept for other reporters.
failures = [l for l in nonempty if l.strip().startswith(('✘', '✗'))][:8]
# The contract: the last 30 non-empty lines of a failing run, with the compiler
# errors and the failing checks hoisted in front of them.
tail = nonempty[-30:]
seen, selected = set(), []
for l in errors + failures + tail:
    if l not in seen:
        selected.append(l)
        seen.add(l)

# The selection is emitted as a short series of capped annotations rather than
# silently truncated to one.
chunks = chunked(selected)
for index, part in enumerate(chunks[:3], 1):
    suffix = f' ({index}/{min(len(chunks), 3)})' if len(chunks) > 1 else ''
    print(f'::error title={prop("macOS Swift tests" + suffix)}::{encode(chr(10).join(part))[:1500]}')
PY
  rm -f "$LOGFILE" ${EVENTS:+"$EVENTS"}
  exit "$SWIFT_EXIT"
fi
rm -f "$LOGFILE" ${EVENTS:+"$EVENTS"}
