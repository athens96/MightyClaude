#!/bin/bash
# Download and verify the pinned CEF and Node runtime archives.
# Cache location: ~/Library/Caches/MightyClaude/browser-engine/<16-hex-lock-prefix>/
# Override with MIGHTY_BROWSER_ENGINE_CACHE.
# Second run with a warm cache prints "cache hit" and exits without downloading.
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LOCK_FILE="$PROJECT_ROOT/native/macos/BrowserEngine.lock"

if [ ! -f "$LOCK_FILE" ]; then
    echo "FAILED: BrowserEngine.lock not found at $LOCK_FILE" >&2
    exit 1
fi

# Derive cache directory from the first 16 hex chars of the lock file's sha256.
LOCK_SHA256="$(shasum -a 256 "$LOCK_FILE" | awk '{print $1}')"
LOCK_PREFIX="${LOCK_SHA256:0:16}"
CACHE_DIR="${MIGHTY_BROWSER_ENGINE_CACHE:-$HOME/Library/Caches/MightyClaude/browser-engine/$LOCK_PREFIX}"
mkdir -p "$CACHE_DIR"

# Parse lock values.
CEF_URL="$(python3 -c "import json,sys; d=json.load(open(sys.argv[1])); print(d['cef']['url'])" "$LOCK_FILE")"
CEF_SHA256="$(python3 -c "import json,sys; d=json.load(open(sys.argv[1])); print(d['cef']['sha256'])" "$LOCK_FILE")"
NODE_URL="$(python3 -c "import json,sys; d=json.load(open(sys.argv[1])); print(d['node']['url'])" "$LOCK_FILE")"
NODE_SHA256="$(python3 -c "import json,sys; d=json.load(open(sys.argv[1])); print(d['node']['sha256'])" "$LOCK_FILE")"

CEF_FILE="$CACHE_DIR/$(basename "$CEF_URL")"
NODE_FILE="$CACHE_DIR/$(basename "$NODE_URL")"

verify_sha256() {
    local file="$1" expected="$2" label="$3"
    local actual
    actual="$(shasum -a 256 "$file" | awk '{print $1}')"
    if [ "$actual" != "$expected" ]; then
        echo "sha256 mismatch for $label" >&2
        echo "  expected: $expected" >&2
        echo "  actual:   $actual" >&2
        return 1
    fi
}

download_archive() {
    local url="$1" dest="$2" label="$3"
    echo "Downloading $label from $url ..."
    if ! curl -fSL --max-time 300 -o "$dest.tmp" "$url"; then
        rm -f "$dest.tmp"
        echo "FAILED: download $label" >&2
        exit 1
    fi
    mv "$dest.tmp" "$dest"
}

check_or_fetch() {
    local file="$1" url="$2" sha256="$3" label="$4"
    if [ -f "$file" ]; then
        if verify_sha256 "$file" "$sha256" "$label" 2>/dev/null; then
            return 0  # already valid
        else
            echo "$label cache mismatch; re-downloading..." >&2
            rm -f "$file"
        fi
    fi
    download_archive "$url" "$file" "$label"
    if ! verify_sha256 "$file" "$sha256" "$label"; then
        rm -f "$file"
        echo "FAILED: sha256 verify $label" >&2
        exit 1
    fi
    return 1  # freshly downloaded
}

CEF_HIT=0
NODE_HIT=0

check_or_fetch "$CEF_FILE" "$CEF_URL" "$CEF_SHA256" "CEF" && CEF_HIT=1
check_or_fetch "$NODE_FILE" "$NODE_URL" "$NODE_SHA256" "Node" && NODE_HIT=1

if [ "$CEF_HIT" -eq 1 ] && [ "$NODE_HIT" -eq 1 ]; then
    echo "cache hit: $CACHE_DIR"
fi

echo "Engine cache ready: $CACHE_DIR"
printf '  CEF:  %s\n' "$(basename "$CEF_FILE")"
printf '  Node: %s\n' "$(basename "$NODE_FILE")"
