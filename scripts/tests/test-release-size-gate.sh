#!/bin/bash
# Proves the release size gate with real files: a 479 MiB package passes, a 481 MiB
# package fails, the 480 MiB boundary itself passes, and a missing package fails. The
# fixtures are sparse (created by seeking, so they occupy almost no disk) but carry the
# apparent length the gate and the updater both measure.
#
# It also checks that the macOS release steps in docs/app-update.md and the macOS
# release workflow call the gate before anything is uploaded.
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
GATE="$PROJECT_ROOT/scripts/check-release-size.sh"
MIB=1048576

fail() {
  echo "FAIL: $1" >&2
  exit 1
}

[ -x "$GATE" ] || fail "scripts/check-release-size.sh is missing or not executable"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/mighty-release-size.XXXXXX")"
cleanup() { rm -rf "$WORK"; }
trap cleanup EXIT

sparse() {
  # $1 = path, $2 = size in MiB. bs=1 count=0 with a seek writes nothing and leaves a
  # hole, so a 481 MiB fixture costs no real bytes.
  dd if=/dev/zero of="$1" bs=1 count=0 seek=$(( $2 * MIB )) 2>/dev/null
  actual="$(stat -f%z "$1" 2>/dev/null || stat -c%s "$1")"
  [ "$actual" -eq $(( $2 * MIB )) ] || fail "fixture $1 is $actual bytes, wanted $(( $2 * MIB ))"
}

# 1. 479 MiB passes.
under="$WORK/MightyClaude-macos-479.zip"
sparse "$under" 479
if ! out="$("$GATE" "$under" 2>&1)"; then
  fail "the gate rejected a 479 MiB package: $out"
fi
grep -q "479.0 MiB" <<<"$out" || fail "the gate did not report the 479 MiB size: $out"
grep -q "^PASS:" <<<"$out" || fail "the gate did not pass a 479 MiB package: $out"

# 2. 481 MiB fails, and says why.
over="$WORK/MightyClaude-macos-481.zip"
sparse "$over" 481
if out="$("$GATE" "$over" 2>&1)"; then
  fail "the gate accepted a 481 MiB package: $out"
fi
grep -q "481.0 MiB" <<<"$out" || fail "the failure did not report the 481 MiB size: $out"
grep -q "480 MiB release gate" <<<"$out" || fail "the failure did not name the 480 MiB gate: $out"
grep -q "512 MiB" <<<"$out" || fail "the failure did not name the 512 MiB updater cap: $out"

# 3. The boundary belongs to the passing side: 480 MiB exactly is accepted, one byte
#    more is not.
boundary="$WORK/MightyClaude-macos-480.zip"
sparse "$boundary" 480
"$GATE" "$boundary" >/dev/null 2>&1 || fail "the gate rejected exactly 480 MiB"
dd if=/dev/zero of="$WORK/over-by-one.zip" bs=1 count=0 seek=$(( 480 * MIB + 1 )) 2>/dev/null
if "$GATE" "$WORK/over-by-one.zip" >/dev/null 2>&1; then
  fail "the gate accepted 480 MiB + 1 byte"
fi

# 4. A single oversized package fails the whole run even when another one is fine.
if "$GATE" "$under" "$over" >/dev/null 2>&1; then
  fail "the gate passed when one of two packages was oversized"
fi

# 5. A package that was never built is a failure, not a silent pass.
if out="$("$GATE" "$WORK/not-built.zip" 2>&1)"; then
  fail "the gate passed for a missing package: $out"
fi
grep -q "does not exist" <<<"$out" || fail "the missing-package failure is unclear: $out"

# 6. The macOS release steps must run the gate before upload.
DOC="$PROJECT_ROOT/docs/app-update.md"
grep -qF 'scripts/check-release-size.sh' "$DOC" \
  || fail "docs/app-update.md does not call scripts/check-release-size.sh"
doc_gate_line="$(grep -n 'scripts/check-release-size.sh' "$DOC" | head -1 | cut -d: -f1)"
doc_upload_line="$(grep -n '^## Cloudflare에 올리기' "$DOC" | head -1 | cut -d: -f1)"
[ -n "$doc_upload_line" ] || fail "docs/app-update.md has no upload section"
[ "$doc_gate_line" -lt "$doc_upload_line" ] \
  || fail "docs/app-update.md mentions the gate only after the upload section"
grep -qF '업로드' "$DOC" || fail "docs/app-update.md no longer describes the upload step"

WORKFLOW="$PROJECT_ROOT/.github/workflows/native-macos.yml"
grep -qF 'scripts/check-release-size.sh' "$WORKFLOW" \
  || fail ".github/workflows/native-macos.yml does not call the release size gate"
wf_gate_line="$(grep -n 'scripts/check-release-size.sh' "$WORKFLOW" | head -1 | cut -d: -f1)"
wf_pack_line="$(grep -n 'ditto -c -k' "$WORKFLOW" | head -1 | cut -d: -f1)"
wf_upload_line="$(awk -v start="$wf_pack_line" 'NR > start && /upload-artifact/ { print NR; exit }' "$WORKFLOW")"
[ -n "$wf_pack_line" ] && [ -n "$wf_upload_line" ] || fail "cannot locate the packaging/upload steps in the macOS workflow"
[ "$wf_gate_line" -gt "$wf_pack_line" ] || fail "the workflow gates the package before it is built"
[ "$wf_gate_line" -lt "$wf_upload_line" ] || fail "the workflow uploads the package before gating its size"

echo "PASS: the 480 MiB release size gate holds (479 MiB passes, 481 MiB fails) and runs before upload"
