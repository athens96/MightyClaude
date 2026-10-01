#!/bin/bash
# Verifies the third-party licence notices: every licence text referenced by
# NOTICE.md exists under licenses/, the WebRTC notices are present in both the
# Korean and the English half of NOTICE.md, and each WebRTC licence file holds
# the upstream licence header and copyright line.
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
NOTICE="$PROJECT_ROOT/NOTICE.md"
LICENSES="$PROJECT_ROOT/licenses"

fail() {
  echo "FAIL: $1" >&2
  exit 1
}

[ -f "$NOTICE" ] || fail "NOTICE.md is missing"
[ -d "$LICENSES" ] || fail "licenses/ is missing"

# 1. Every licences/<file> path mentioned in NOTICE.md must exist and be non-empty.
referenced="$(grep -o 'licenses/[A-Za-z0-9._-]*\.txt' "$NOTICE" | sort -u)"
[ -n "$referenced" ] || fail "NOTICE.md references no licence text file"
while IFS= read -r rel; do
  [ -s "$PROJECT_ROOT/$rel" ] || fail "$rel is referenced by NOTICE.md but missing or empty"
done <<< "$referenced"

# 2. The WebRTC licence texts must be present with the upstream header/copyright.
#    format: <file>|<expected string 1>|<expected string 2>
while IFS='|' read -r file first second; do
  path="$LICENSES/$file"
  [ -s "$path" ] || fail "licenses/$file is missing or empty"
  grep -qF "$first" "$path" || fail "licenses/$file does not contain: $first"
  grep -qF "$second" "$path" || fail "licenses/$file does not contain: $second"
  grep -qF "licenses/$file" "$NOTICE" || fail "NOTICE.md does not reference licenses/$file"
done <<'ROWS'
WebRTC-BSD-3-Clause.txt|Copyright (c) 2011, The WebRTC project authors. All rights reserved.|Neither the name of Google nor the names of its contributors
stasel-WebRTC-BSD-3-Clause.txt|BSD 3-Clause License|Copyright (c) 2011, The WebRTC project authors. All rights reserved.
livekit-webrtc-xcframework-MIT.txt|MIT License|Copyright (c) 2021 WebRTC SDKs
react-native-webrtc-MIT.txt|The MIT License (MIT)|Copyright (c) 2017-present React Native WebRTC Community
ROWS

# 3. Both WebRTC licence families must be disclaimed in NOTICE.md, and the
#    notice must name every WebRTC component the feature can ship.
for needle in \
  'WebRTC — BSD 3-Clause / MIT' \
  'https://webrtc.googlesource.com/src' \
  'stasel/WebRTC' \
  'livekit/webrtc-xcframework' \
  'react-native-webrtc' \
  '@config-plugins/react-native-webrtc' \
  'Copyright (c) 2015-2017 Howard Yang'
do
  grep -qF -- "$needle" "$NOTICE" || fail "NOTICE.md does not mention: $needle"
done

# 4. The WebRTC notice must appear in both halves: before and after the English
#    heading that splits the file.
split_line="$(grep -n '^# NOTICE (English)' "$NOTICE" | head -1 | cut -d: -f1)"
[ -n "$split_line" ] || fail "NOTICE.md has no English half"
ko_hits="$(head -n "$split_line" "$NOTICE" | grep -c '^## WebRTC ' || true)"
en_hits="$(tail -n "+$split_line" "$NOTICE" | grep -c '^## WebRTC ' || true)"
[ "$ko_hits" -eq 1 ] || fail "the Korean half has $ko_hits WebRTC sections (want 1)"
[ "$en_hits" -eq 1 ] || fail "the English half has $en_hits WebRTC sections (want 1)"

# 5. The notice that travels inside the app bundle must match the repo text,
#    because scripts/build-macos.sh copies native/licenses/ into the .app.
BUNDLED="$PROJECT_ROOT/native/licenses/WebRTC-LICENSE"
[ -s "$BUNDLED" ] || fail "native/licenses/WebRTC-LICENSE is missing or empty"
cmp -s "$BUNDLED" "$LICENSES/WebRTC-BSD-3-Clause.txt" \
  || fail "native/licenses/WebRTC-LICENSE differs from licenses/WebRTC-BSD-3-Clause.txt"
grep -qF 'native/licenses' "$PROJECT_ROOT/scripts/build-macos.sh" \
  || fail "build-macos.sh no longer copies native/licenses into the app bundle"

echo "PASS: WebRTC licence notices are complete in NOTICE.md, licenses/ and the app bundle"
