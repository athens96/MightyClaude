#!/usr/bin/env bash
# Usage: scripts/help/upload.sh [--dry-run] [site-dir]
#
# Uploads the built help site (node scripts/help/build.mjs --shots <dir>) to the R2 bucket
# `mightyclaude` under `mightyclaude/help/`, the way releases are uploaded:
# `wrangler r2 object put <bucket>/<key> --file … --remote` (wrangler 4 writes to a local
# simulator without --remote). wrangler signs in through its own OAuth login; this script
# reads no keys and prints only the object keys it writes.
#
# Order: pictures first, then the language pages, then the root redirect, so a page is never
# live before its pictures. Each `<dir>/index.html` is also written at the key `<dir>/`,
# because the public bucket URL does not add index.html (the apps open …/help/<lang>/).
# Pages are no-cache so a fix shows at once; pictures are cached for an hour.
set -euo pipefail

BUCKET="mightyclaude"
PREFIX="mightyclaude/help"
DRY_RUN=0
SITE=""
for arg in "$@"; do
  case "$arg" in
    --dry-run) DRY_RUN=1 ;;
    -h|--help) sed -n '2,14p' "$0"; exit 0 ;;
    *) SITE="$arg" ;;
  esac
done
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SITE="${SITE:-$ROOT/docs/help/site}"

if [ ! -f "$SITE/index.html" ]; then
  echo "help upload: $SITE/index.html not found; run node scripts/help/build.mjs --shots <dir> first" >&2
  exit 1
fi
if [ "$DRY_RUN" -eq 0 ] && ! command -v wrangler >/dev/null 2>&1; then
  echo "help upload: wrangler is not installed" >&2
  exit 1
fi
# Refuse to publish a site whose content no longer passes the checks.
node "$ROOT/scripts/help/build.mjs" --check >/dev/null

content_type() {
  case "$1" in
    *.html) echo "text/html; charset=utf-8" ;;
    *.webp) echo "image/webp" ;;
    *.png)  echo "image/png" ;;
    *.css)  echo "text/css; charset=utf-8" ;;
    *.js)   echo "text/javascript; charset=utf-8" ;;
    *.json) echo "application/json" ;;
    *.svg)  echo "image/svg+xml" ;;
    *) echo "" ;;
  esac
}

put() { # put <file> <key>
  local file="$1" key="$2" type cache
  type="$(content_type "$file")"
  if [ -z "$type" ]; then
    echo "help upload: no content type for $file" >&2
    exit 1
  fi
  case "$file" in
    *.html) cache="no-cache" ;;
    *) cache="public, max-age=3600" ;;
  esac
  echo "put $BUCKET/$key ($type)"
  if [ "$DRY_RUN" -eq 0 ]; then
    wrangler r2 object put "$BUCKET/$key" --file "$file" --content-type "$type" --cache-control "$cache" --remote >/dev/null
  fi
}

cd "$SITE"
count=0
# 1. pictures and any other non-page files
while IFS= read -r -d '' file; do
  put "$file" "$PREFIX/${file#./}"; count=$((count + 1))
done < <(find . -type f ! -name '*.html' ! -name '.*' -print0 | sort -z)
# 2. language pages, each also at its folder key
while IFS= read -r -d '' file; do
  rel="${file#./}"
  put "$file" "$PREFIX/$rel"
  put "$file" "$PREFIX/$(dirname "$rel")/"
  count=$((count + 2))
done < <(find . -mindepth 2 -type f -name '*.html' -print0 | sort -z)
# 3. the root redirect last
put "index.html" "$PREFIX/index.html"
put "index.html" "$PREFIX/"
count=$((count + 2))

if [ "$DRY_RUN" -eq 1 ]; then
  echo "help upload: dry run, $count objects listed, nothing sent"
else
  echo "help upload: $count objects sent"
fi
