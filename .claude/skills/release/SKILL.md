---
name: release
description: Releases Mighty Code with scripts/release.py, the notes written by the release-notes agent and a failed CI run read by the ci-failure agent. Use when the user asks for a release, e.g. "릴리즈 해", "0.4.56 릴리즈해 줘", "release".
argument-hint: "[version]"
---

# Release

`docs/release.md` is the procedure: what every step checks and every option. This is the order this repository runs it in.

## Before anything

- On `main`, clean tree, up to date with `origin/main` (`git status`, `git fetch`).
- Windows and phone parity is done for what ships: ask the user when you cannot tell.
- The version: the one the user named, else the next patch after `VERSION`. Say which before step 1.
- The user asked for this release: that is what allows the `--yes` steps. Without it, run none of them.

## Steps

1. `python scripts/release.py check --version <version>` makes the two note templates.
2. Start the `release-notes` agent in the background with the version, and go on.
3. `python scripts/release.py version --summary "<one line>"` makes the release commit (not pushed).
4. `python scripts/release.py push --yes`.
5. Start these as background commands, side by side:
   - `python scripts/release.py ci`
   - only when the phone app changed since the last release (`git diff --stat <last "(release) x.y.z:" commit> -- mobile`): the EAS preview APK in `mobile/`, `npx eas-cli build -p android --profile preview --non-interactive` (with `--local --output release/<version>/app.apk` to build it on this machine).
6. When the notes agent reports, read both note files and correct what needs it.
7. CI failed: hand the run to the `ci-failure` agent, fix the cause, commit and push the fix, then `python scripts/release.py ci --commit <fix sha>` in the background again.
8. When the notifications say CI (and the APK) are done, in order: `download` → `manifest` (with `--apk <path>` for a new APK) → `page` (show its diff) → `upload --dry-run` → `upload --yes` → `github --yes` → `verify` → `scripts/help/upload.sh` (`--dry-run` first) when the help pages changed.
9. Offer to update this Mac's app to the new version (its own update, which restarts the app); only on the user's yes.
