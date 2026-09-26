# macOS Resources Regression — Investigation Write-up

## Summary

Build 298 of MightyClaude was installed and showed raw locale keys (e.g.
`settings.language` instead of "언어") everywhere and no companion pet (the
`mighty-raccoon` sprite was absent). This document records what was inspected,
the conclusion, and what was added to prevent recurrence.

---

## What was inspected

### Build path
- `scripts/build-macos.sh` — copies binary, `*.bundle` resource bundles, `pets/`,
  icons, Info.plist, then signs.
- `native/macos/Package.swift` — `MightyCore` target declares
  `.copy("Resources/Styles")` and `.copy("Resources/Locales")` which SwiftPM
  copies into `MightyClaude_MightyCore.bundle`. The `en.json` and `ko.json`
  files live under `native/macos/Sources/MightyCore/Resources/Locales/`.

### Locale lookup path (`LocaleBundle.swift`)
`loadCatalog` walks these candidate paths in order:
1. Inside `MightyClaude_MightyCore.bundle/Contents/Resources/Locales/` (macOS SwiftPM layout)
2. Inside `MightyClaude_MightyCore.bundle/Locales/` (flat layout)
3. `Bundle.main.resourceURL/Locales/`
4. `Bundle.main.resourceURL/` (bare filename)
5. `locales/<lang>.json` relative to cwd

An empty JSON object `{}` was silently accepted as a non-empty catalog in the
pre-fix code (the `[String: String]` deserialization of `{}` succeeds and returns
`[:]`, which `loadCatalog` returned without warning).

### Pet lookup path (`CompanionPet.loadAvailable`)
The default pet is searched at:
1. `Bundle.main.resourceURL/pets/mighty-raccoon`
2. `cwd/assets/pets/mighty-raccoon`
3. `cwd/../../assets/pets/mighty-raccoon`

A missing pet was silently ignored and the list was returned without it.

### Packaging gate before this fix
`build-macos.sh` already had a gate for bundled style manifests
(`ouroboros.json`, `paperthin.json`) but no gate for locale catalogs or the
default pet.

### LaunchServices candidate for build 298
`install-macos.sh` notes that every copy of the bundle (build output, backup,
`/Applications`) registers under the same bundle ID with LaunchServices. If the
wrong copy — a stale build output with a missing or empty locale bundle — was
launched instead of the freshly installed one, raw keys would appear without any
source defect in the installed app itself.

---

## Root cause

**No source defect was found in the locale or pet packaging code at HEAD.**

The most likely environmental cause for build 298 is a LaunchServices resolution
that resolved the stale build-output copy instead of the newly installed
`/Applications/MightyClaude.app`. The build output registered under
`dev.mightyclaude.native` alongside the backup and the installed copy.
`install-macos.sh` does call `lsregister -u $SOURCE` and `lsregister -f
$DESTINATION` but the order of registration events in the system database is
not guaranteed, and a fresh launch via Spotlight or Dock could still pick the
stale copy in rare race windows.

A secondary candidate is that the `MightyClaude_MightyCore.bundle` was copied
with a SwiftPM flat layout (`Locales/ko.json` at the root of the bundle) on one
build and a macOS layout (`Contents/Resources/Locales/ko.json`) on another, and
both paths were present with one being empty or stale.

---

## What was changed

### Guard (`--verify-resources`)
`MightyClaudeApp.main()` checks for `--verify-resources` as the **very first
statement**, before CEF initialisation, NSApplication, IME, or LaunchServices
work. The binary can therefore be called headlessly by scripts.

`ResourceVerifier.run()` calls `ResourceHealthChecker` for:
- `ko.json` catalog
- `en.json` catalog
- `pets/mighty-raccoon` default pet

For each resource it prints the resolved path or `MISSING` with the search paths
tried. It prints `VERIFY_RESOURCES_OK` and exits 0 only when all three resolve.
An empty JSON object is treated as a miss.

`scripts/build-macos.sh` runs the guard on the packaged `.app` after signing and
fails the build if it exits non-zero.

`scripts/install-macos.sh` runs the guard on `SOURCE` before waiting for the
running app to quit, and refuses to install if it exits non-zero.

`scripts/verify-resources-selftest.sh` runs three outcome checks: good build →
exit 0 + `VERIFY_RESOURCES_OK`; Locales removed → exit non-zero + resource
named; pet removed → exit non-zero + resource named. Prints `RESOURCES_GUARD_OK`
only when all three pass.

### Runtime hardening
`ResourceHealthChecker.checkCatalog` replaces the duplicated search logic in
`loadCatalog` so the running app and the guard walk exactly the same candidate
list.

When `loadCatalog` cannot resolve a non-empty catalog it calls
`ResourceHealthChecker.logWarning` which emits an NSLog line naming the resource
and every path tried.

When `CompanionPet.loadAvailable` cannot find `mighty-raccoon` after the full
search it calls `ResourceHealthChecker.logWarning` for the same effect.

`AppStore.checkResourceHealth()` is called immediately after `isLoaded = true`
in `load()`. If any catalog or the default pet is missing it sets
`AppStore.resourceWarning` to a built-in (not catalog-sourced) one-line message.
`WorkspaceView` shows this as a dismissible yellow banner.

The app still starts and falls back to raw keys / no pet exactly as before.
