#!/bin/bash
# Usage (a workflow's `changes` job): bash scripts/ci-changes.sh macos|windows
#
# Writes to $GITHUB_OUTPUT whether this run builds and tests that platform (`run`) and whether it captures the
# help screenshots (`help`, macOS only):
#   - workflow_dispatch: everything; help unless the `full` input is false (release.py dispatches a full run
#     when the build commit's push run skipped the build).
#   - pull_request: run, no help.
#   - push of a release commit ("(release) x.y.z: ..."): run and help (the help site's pictures come from it).
#   - any other push: no help, and the platform is skipped when every changed file belongs to the other one only
#     (macOS: everything under native/windows/; Windows: everything under native/macos/ or relay/). Anything else,
#     or a push whose range cannot be read (a new branch, a force-push), runs.
# Env: EVENT, BEFORE (github.event.before), MESSAGE (the head commit's message), FULL (the dispatch input), GITHUB_SHA.
set -euo pipefail

platform="${1:?macos or windows}"
run=true
help=false

case "${EVENT:-push}" in
  workflow_dispatch)
    [[ "${FULL:-true}" != "false" ]] && help=true
    ;;
  pull_request)
    ;;
  *)
    if [[ "${MESSAGE:-}" == "(release) "* ]]; then
      help=true
    elif [[ -n "${BEFORE:-}" && ! "${BEFORE}" =~ ^0+$ ]] && git cat-file -e "${BEFORE}^{commit}" 2>/dev/null; then
      files="$(git diff --name-only "$BEFORE" "$GITHUB_SHA")"
      if [[ -n "$files" ]]; then
        case "$platform" in
          macos) other='^native/windows/' ;;
          windows) other='^(native/macos/|relay/)' ;;
          *) echo "unknown platform $platform" >&2; exit 2 ;;
        esac
        if ! grep -Eqv "$other" <<<"$files"; then
          run=false
        fi
      fi
    fi
    ;;
esac

[[ "$platform" == macos ]] || help=false
echo "$platform: run=$run help=$help (event ${EVENT:-push})"
{
  echo "run=$run"
  echo "help=$help"
} >> "${GITHUB_OUTPUT:-/dev/stdout}"
