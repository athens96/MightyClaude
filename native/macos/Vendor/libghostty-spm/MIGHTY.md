# Vendored libghostty-spm

Upstream: https://github.com/Lakr233/libghostty-spm, tag `1.5.20260906`
(revision `733ae3b29d447b6707cbfc00879027a076dfd0eb`). The prebuilt
`GhosttyKit.xcframework` still downloads from upstream's release by URL and checksum.

Removed from the copy: `Tests/` (and the test target in `Package.swift`), `Example/`,
`Patches/`, `Script/`, `docs/`, `build.sh`, the alternate manifests and the agent notes.

The one source patch is in `Sources/GhosttyTerminal/Configuration/GhosttyRuntimeResources.swift`:
SwiftPM's generated `Bundle.module` looks only beside the `.app` and at the build
machine's absolute `.build` path, then traps. An app built on CI and installed
elsewhere has neither, so opening the first terminal crashed it (0.4.50). The patch
also looks in `Contents/Resources`, where `scripts/build-macos.sh` puts the bundle,
and returns nil instead of trapping.

To update: copy the new tag over this folder, apply the same trims, and re-apply the
patch (or drop the vendoring if upstream stops using `Bundle.module` there).
