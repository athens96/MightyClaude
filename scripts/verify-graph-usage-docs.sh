#!/usr/bin/env bash
# Verifies that docs/model-defaults.md carries the graph-usage attribution section
# and the four required 기기 미확인 rows, and that docs/i18n.md is current.
# Prints GRAPH_USAGE_DOCS_OK on success; exits non-zero on failure.
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

# 1. Attribution section present
check "section header"  docs/model-defaults.md "Model and token display on the graph"
check "blockCapsule"    docs/model-defaults.md "ModelUsageFormat.blockCapsule"
check "activitySuffix"  docs/model-defaults.md "ModelUsageFormat.activitySuffix"
check "sameResponse key" docs/model-defaults.md "usage.modelUsage.sameResponse"

# 2. Four 기기 미확인 rows for the new graph-usage items
check "capsule 기기 미확인"    docs/model-defaults.md "블록 캡슐 모델 표시"
check "activity line 기기 미확인" docs/model-defaults.md "활동 줄 모델·토큰 표시"
check "same-response 기기 미확인" docs/model-defaults.md "같은 응답 마커"
check "subagent 기기 미확인"   docs/model-defaults.md "서브에이전트 활동 줄 합계"

# 3. docs/i18n.md must match what check-locales.js would generate
if ! node "$ROOT/scripts/check-locales.js" --check > /dev/null 2>&1; then
  echo "FAIL: docs/i18n.md is out of date; run node scripts/check-locales.js" >&2
  FAILURES=$((FAILURES + 1))
fi

if [ "$FAILURES" -gt 0 ]; then
  echo "$FAILURES check(s) failed" >&2
  exit 1
fi
echo "GRAPH_USAGE_DOCS_OK"
