import CryptoKit
import Foundation
import Testing
@testable import MightyCore

@Suite(.serialized) struct AppUpdateTests {
    @Test func versionsCompareNumericallyWithPrereleasesBelowReleases() {
        #expect(AppVersion.isNewer("0.2.0", than: "0.1.0"))
        #expect(AppVersion.isNewer("0.10.0", than: "0.9.9"))
        #expect(AppVersion.isNewer("1.0", than: "0.99.99.99"))
        #expect(!AppVersion.isNewer("0.1.0", than: "0.1.0"))
        #expect(!AppVersion.isNewer("0.1", than: "0.1.0"))
        #expect(AppVersion.isNewer("1.0.0", than: "1.0.0-beta.2"))
        #expect(AppVersion.isNewer("1.0.0-beta.10", than: "1.0.0-beta.2"))
        #expect(AppVersion.isNewer("v2.0.0", than: "1.9.0"))
        #expect(AppVersion.normalized("1.0.0+build.7") == "1.0.0+build.7")
        #expect(AppVersion.normalized("latest") == nil && AppVersion.normalized("1.2.3.4.5") == nil && AppVersion.normalized("1..2") == nil)
        #expect(!AppVersion.isNewer("garbage", than: "0.1.0"))
        #expect(AppVersion.normalized(AppVersion.systemVersion) != nil)
    }

    @Test func manifestParsingAcceptsTheDocumentedShapeAndVariants() throws {
        let documented = Data(#"{"version":"0.2.0","build":42,"notes":"Fixes","publishedAt":"2026-09-18T00:00:00Z","minimumSystemVersion":"14.0","macos":{"url":"https://cdn.example.com/m/MightyClaude-macos.zip","sha256":"SHA256:AB"# .utf8) + Data(String(repeating: "c", count: 62).utf8) + Data(#"","size":123},"windows":{"x64":{"url":"https://cdn.example.com/w/x64.zip"},"arm64":"https://cdn.example.com/w/arm64.zip"}}"#.utf8)
        let manifest = try AppUpdateManifest.parse(documented)
        #expect(manifest.version == "0.2.0" && manifest.build == 42 && manifest.notes == "Fixes" && manifest.minimumSystemVersion == "14.0" && !manifest.signed)
        #expect(manifest.macos?.url.absoluteString == "https://cdn.example.com/m/MightyClaude-macos.zip" && manifest.macos?.size == 123)
        #expect(manifest.macos?.sha256 == "ab" + String(repeating: "c", count: 62))
        // Rule 2: windows assets without sha256 and size are rejected.
        #expect(manifest.windows["x64"] == nil && manifest.windows["arm64"] == nil)
        // Rule 2: a bare-URL asset (no sha256/size) is always rejected.
        let variant = try AppUpdateManifest.parse(Data(#"{"latest":"v0.3.0","platforms":{"mac":"https://cdn.example.com/a.zip"}}"#.utf8))
        #expect(variant.version == "0.3.0" && variant.macos == nil)
        // Bad digests are ignored, non-https packages dropped, missing versions rejected.
        let loose = try AppUpdateManifest.parse(Data(#"{"version":"0.2.0","macos":{"url":"http://cdn.example.com/a.zip","sha256":"zz"}}"#.utf8))
        #expect(loose.macos == nil)
        // file URLs are only allowed under the test flag, and only when sha256 and size are present.
        let fileDigest = String(repeating: "a", count: 64)
        #expect(try AppUpdateManifest.parse(Data(#"{"version":"0.2.0","macos":"file:///tmp/a.zip"}"#.utf8)).macos == nil)
        #expect(try AppUpdateManifest.parse(Data(#"{"version":"0.2.0","macos":{"url":"file:///tmp/a.zip","sha256":"\#(fileDigest)","size":100}}"#.utf8)).macos == nil)
        #expect(try AppUpdateManifest.parse(Data(#"{"version":"0.2.0","macos":{"url":"file:///tmp/a.zip","sha256":"\#(fileDigest)","size":100}}"#.utf8), allowsFileURLs: true).macos != nil)
        #expect(throws: MightyError.self) { try AppUpdateManifest.parse(Data(#"{"macos":{"url":"https://x/a.zip"}}"#.utf8)) }
        #expect(throws: MightyError.self) { try AppUpdateManifest.parse(Data("[1,2]".utf8)) }
        #expect(throws: MightyError.self) { try AppUpdateManifest.parse(Data(repeating: 0x20, count: 300 * 1024)) }
    }

    @Test func signedEnvelopesVerifyAgainstTheBuildsPublicKey() throws {
        let key = Curve25519.Signing.PrivateKey()
        let plain = Data(#"{"version":"0.5.0","macos":"https://cdn.example.com/a.zip"}"#.utf8)
        let signature = try key.signature(for: plain)
        func envelope(_ signature: Data, format: String = AppUpdateManifest.envelopeFormat) -> Data {
            Data(#"{"format":"\#(format)","payload":"\#(plain.base64EncodedString())","signature":"\#(signature.base64EncodedString())"}"#.utf8)
        }
        let verified = try AppUpdateManifest.parse(envelope(signature), publicKey: key.publicKey.rawRepresentation)
        #expect(verified.version == "0.5.0" && verified.signed)
        // Without a key the envelope is readable but not marked verified; a plain manifest is refused when a key is set.
        #expect(try AppUpdateManifest.parse(envelope(signature)).signed == false)
        #expect(throws: MightyError.self) { try AppUpdateManifest.parse(plain, publicKey: key.publicKey.rawRepresentation) }
        // Wrong key, tampered signature, wrong format all fail.
        let other = Curve25519.Signing.PrivateKey()
        #expect(throws: MightyError.self) { try AppUpdateManifest.parse(envelope(signature), publicKey: other.publicKey.rawRepresentation) }
        var broken = signature; broken[3] ^= 0xFF
        #expect(throws: MightyError.self) { try AppUpdateManifest.parse(envelope(broken), publicKey: key.publicKey.rawRepresentation) }
        #expect(throws: MightyError.self) { try AppUpdateManifest.parse(envelope(signature, format: "other"), publicKey: key.publicKey.rawRepresentation) }
    }

    @Test func availabilityHonoursTheMinimumSystemVersionAndRedirectsStayOnHTTPS() {
        let manifest = AppUpdateManifest(version: "0.9.0", minimumSystemVersion: "99.0", macos: AppUpdateAsset(url: URL(string: "https://x/a.zip")!))
        let blocked = AppUpdateAvailability(current: "0.1.0", manifest: manifest, systemVersion: "15.6.0")
        #expect(!blocked.isNewer && blocked.blockedReason?.contains("99.0") == true)
        let fine = AppUpdateAvailability(current: "0.1.0", manifest: AppUpdateManifest(version: "0.9.0", minimumSystemVersion: "14.0"), systemVersion: "15.6.0")
        #expect(fine.isNewer && fine.blockedReason == nil)
        #expect(!AppUpdateAvailability(current: "0.9.0", manifest: manifest, systemVersion: "1.0").isNewer)
        let policy = AppUpdateTransportPolicy(allowsFileURLs: false)
        let session = URLSession.shared, task = session.dataTask(with: URL(string: "https://x/")!)
        let response = HTTPURLResponse(url: URL(string: "https://x/")!, statusCode: 302, httpVersion: nil, headerFields: nil)!
        var decision: URLRequest?? = nil
        policy.urlSession(session, task: task, willPerformHTTPRedirection: response, newRequest: URLRequest(url: URL(string: "http://x/plain")!)) { decision = .some($0) }
        #expect(decision == .some(nil))
        policy.urlSession(session, task: task, willPerformHTTPRedirection: response, newRequest: URLRequest(url: URL(string: "https://cdn.x/next")!)) { decision = .some($0) }
        #expect(decision??.url?.host == "cdn.x")
    }

    private func makeFakeApp(at root: URL, executable: String = "MightyClaude", bundleId: String = "dev.mightyclaude.native", version: String = "0.2.0") throws -> URL {
        let app = root.appendingPathComponent("MightyClaude.app", isDirectory: true)
        try FileManager.default.createDirectory(at: app.appendingPathComponent("Contents/MacOS"), withIntermediateDirectories: true)
        let plist: [String: Any] = ["CFBundleIdentifier": bundleId, "CFBundleExecutable": executable, "CFBundleShortVersionString": version]
        try PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0).write(to: app.appendingPathComponent("Contents/Info.plist"))
        let binary = app.appendingPathComponent("Contents/MacOS/" + executable)
        try Data("#!/bin/sh\necho hi\n".utf8).write(to: binary)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: binary.path)
        return app
    }
    private func zip(_ item: URL, to zip: URL) async throws {
        let result = try await AppUpdateService.run("/usr/bin/ditto", ["-c", "-k", "--keepParent", item.path, zip.path])
        #expect(result.exitCode == 0)
    }

    @Test func downloadVerifiesDigestAndSizeThenStagingValidatesTheBundle() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("app-update-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let source = root.appendingPathComponent("source", isDirectory: true)
        let app = try makeFakeApp(at: source)
        let package = root.appendingPathComponent("MightyClaude-macos.zip")
        try await zip(app, to: package)
        let data = try Data(contentsOf: package)
        let digest = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
        let updates = root.appendingPathComponent("updates", isDirectory: true)
        let manifestFile = root.appendingPathComponent("latest.json")
        let plainPayload = Data(#"{"version":"0.2.0","macos":{"url":"\#(package.absoluteString)","sha256":"\#(digest)","size":\#(data.count)}}"#.utf8)

        // Rule 1: a keyless service refuses check() with a clear error.
        let keylessService = AppUpdateService(directory: updates, allowsFileURLs: true)
        #expect(!(await keylessService.verifiesSignatures))
        try plainPayload.write(to: manifestFile)
        await #expect(throws: MightyError.self) { try await keylessService.check(manifestURL: manifestFile, currentVersion: "0.1.0") }

        // Successful check requires a keyed service and a signed envelope.
        let key = Curve25519.Signing.PrivateKey()
        let sig = try key.signature(for: plainPayload)
        let envelope = Data(("{\"format\":\"\(AppUpdateManifest.envelopeFormat)\",\"payload\":\"\(plainPayload.base64EncodedString())\",\"signature\":\"\(sig.base64EncodedString())\"}").utf8)
        try envelope.write(to: manifestFile)
        let service = AppUpdateService(directory: updates, publicKey: key.publicKey.rawRepresentation, allowsFileURLs: true)
        #expect(await service.verifiesSignatures)

        let availability = try await service.check(manifestURL: manifestFile, currentVersion: "0.1.0")
        #expect(availability.isNewer && availability.manifest.macos?.size == data.count)
        #expect(!(try await service.check(manifestURL: manifestFile, currentVersion: "0.2.0")).isNewer)
        let asset = try #require(availability.manifest.macos)
        try FileManager.default.createDirectory(at: updates.appendingPathComponent("0.0.9"), withIntermediateDirectories: true)
        let downloaded = try await service.download(asset, version: "0.2.0")
        #expect(try Data(contentsOf: downloaded) == data)
        #expect(downloaded.path == updates.appendingPathComponent("0.2.0/MightyClaude-macos.zip").path)
        #expect(!FileManager.default.fileExists(atPath: updates.appendingPathComponent("0.0.9").path)) // older folders pruned

        // A wrong digest or size is refused and nothing is kept; http is refused outright.
        await #expect(throws: MightyError.self) { try await service.download(AppUpdateAsset(url: package, sha256: String(repeating: "0", count: 64)), version: "0.2.1") }
        await #expect(throws: MightyError.self) { try await service.download(AppUpdateAsset(url: package, size: data.count + 1), version: "0.2.2") }
        #expect(!FileManager.default.fileExists(atPath: updates.appendingPathComponent("0.2.1").path))
        await #expect(throws: MightyError.self) { try await service.download(AppUpdateAsset(url: URL(string: "http://example.com/a.zip")!), version: "0.2.3") }
        let good = try await service.download(asset, version: "0.2.0")

        // Staging finds the bundle and checks its identity; symlinked bundles are refused.
        let staged = try await service.stage(package: good, expectedBundleIdentifier: "dev.mightyclaude.native")
        #expect(staged.lastPathComponent == "MightyClaude.app" && FileManager.default.isExecutableFile(atPath: staged.appendingPathComponent("Contents/MacOS/MightyClaude").path))
        await #expect(throws: MightyError.self) { try await service.stage(package: good, expectedBundleIdentifier: "com.example.other") }
        let other = root.appendingPathComponent("other", isDirectory: true)
        let wrong = try makeFakeApp(at: other, bundleId: "com.example.other")
        let wrongZip = root.appendingPathComponent("wrong.zip"); try await zip(wrong, to: wrongZip)
        await #expect(throws: MightyError.self) { try await service.stage(package: wrongZip, expectedBundleIdentifier: "dev.mightyclaude.native") }
        let linkRoot = root.appendingPathComponent("links", isDirectory: true)
        try FileManager.default.createDirectory(at: linkRoot, withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(at: linkRoot.appendingPathComponent("MightyClaude.app"), withDestinationURL: app)
        #expect(throws: MightyError.self) { try AppUpdateService.validate(app: linkRoot.appendingPathComponent("MightyClaude.app"), within: linkRoot, expectedBundleIdentifier: "dev.mightyclaude.native") }
        // Archive the folder itself so the link is an entry of the zip, not resolved by ditto.
        let linkZip = root.appendingPathComponent("link.zip")
        let archived = try await AppUpdateService.run("/usr/bin/ditto", ["-c", "-k", linkRoot.path, linkZip.path])
        #expect(archived.exitCode == 0)
        await #expect(throws: MightyError.self) { try await service.stage(package: linkZip, expectedBundleIdentifier: "dev.mightyclaude.native") }
        // A link to the plist inside an otherwise real bundle is refused too.
        let plistLinked = try makeFakeApp(at: root.appendingPathComponent("plist-link", isDirectory: true))
        try FileManager.default.removeItem(at: plistLinked.appendingPathComponent("Contents/Info.plist"))
        try FileManager.default.createSymbolicLink(at: plistLinked.appendingPathComponent("Contents/Info.plist"), withDestinationURL: app.appendingPathComponent("Contents/Info.plist"))
        #expect(throws: MightyError.self) { try AppUpdateService.validate(app: plistLinked, expectedBundleIdentifier: "dev.mightyclaude.native") }
        // Manifest addresses must be https when file URLs are not allowed (keyed service, no allowsFileURLs).
        let strict = AppUpdateService(directory: updates, publicKey: key.publicKey.rawRepresentation)
        await #expect(throws: MightyError.self) { try await strict.check(manifestURL: manifestFile, currentVersion: "0.1.0") }
    }

    @Test func installHelperSwapsTheBundleKeepsNoBackupOnSuccessAndLogs() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("app-install-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let staged = try makeFakeApp(at: root.appendingPathComponent("updates/0.2.0/staged", isDirectory: true), version: "0.2.0")
        let installed = try makeFakeApp(at: root.appendingPathComponent("Apps (it's here)", isDirectory: true), version: "0.1.0")
        let package = root.appendingPathComponent("updates/0.2.0/MightyClaude-macos.zip")
        try Data("zip".utf8).write(to: package)
        // The helper waits up to five minutes for this pid to go away, so it must be one that is
        // certainly gone: a fixed number can belong to a live process of the same user on a busy
        // runner. A child that has exited and been reaped frees its pid, and macOS hands pids out
        // in rising order, so it is not reused before the helper looks.
        let gone = Process()
        gone.executableURL = URL(fileURLWithPath: "/usr/bin/true")
        try gone.run(); gone.waitUntilExit()
        let pid = gone.processIdentifier
        #expect(kill(pid, 0) == -1 && errno == ESRCH)
        let script = AppUpdateService.installScript(stagedApp: staged, destination: installed, pid: pid, relaunch: false)
        #expect(script.hasPrefix("#!/bin/sh") && script.contains("PATH=/usr/bin:/bin") && script.contains("PID=\(pid)\n") && script.contains("kill -0 \"$PID\""))
        #expect(script.contains("OLD=\"$DESTINATION.update-old\"") && script.contains("lsregister") && script.contains("\nsleep 2\n"))
        #expect(script.contains("DESTINATION='" + root.path + "/Apps (it'\\''s here)/MightyClaude.app'"))
        #expect(!script.contains("open -n") && AppUpdateService.installScript(stagedApp: staged, destination: installed, pid: 1).contains("open -n \"$DESTINATION\""))
        // Run the real helper: the pid is gone, so it proceeds at once. Its own last word is the
        // completion signal: "Installed: " once the swap is done, or one of its failure lines.
        // The swap (a fixed two-second pause, two ditto copies, two lsregister calls) takes a few
        // seconds here but far longer on a loaded three-core CI runner, so the bound is generous;
        // a pass returns as soon as the line appears.
        let service = AppUpdateService(directory: root.appendingPathComponent("updates"), allowsFileURLs: true)
        try await service.launchInstaller(script: script, near: package)
        let log = root.appendingPathComponent("updates/0.2.0/install.log")
        let failures = ["skipping the update", "No app to install", "Could not copy", "Could not back up", "restoring the previous version"]
        var finished = false, failed = false, text = ""
        let deadline = ContinuousClock.now + .seconds(240)
        while ContinuousClock.now < deadline {
            text = (try? String(contentsOf: log, encoding: .utf8)) ?? ""
            if text.contains("Installed: ") { finished = true; break }
            if failures.contains(where: text.contains) { failed = true; break }
            try await Task.sleep(for: .milliseconds(100))
        }
        #expect(finished, "the install helper \(failed ? "failed" : "did not finish within 240 s"); install.log: \(text)")
        let plist = try PropertyListSerialization.propertyList(from: Data(contentsOf: installed.appendingPathComponent("Contents/Info.plist")), format: nil) as? [String: Any]
        #expect(plist?["CFBundleShortVersionString"] as? String == "0.2.0")
        #expect(!FileManager.default.fileExists(atPath: staged.path)) // staged folder removed
        #expect(!FileManager.default.fileExists(atPath: installed.path + ".update-new"))
        #expect(!FileManager.default.fileExists(atPath: installed.path + ".update-old")) // the old app is gone once the swap succeeded
    }

    /// Installing when the app quits: the helper does not start the app again, and if a copy of
    /// the app is running from the destination by the time it would swap, it leaves the bundle
    /// alone (the update stays staged for the next quit).
    @Test func installOnQuitNeitherRelaunchesNorSwapsUnderARunningApp() {
        let staged = URL(fileURLWithPath: "/tmp/updates/0.2.0/staged/MightyClaude.app")
        let destination = URL(fileURLWithPath: "/Applications/Mighty (it's) [1].app")
        let quiet = AppUpdateService.installScript(stagedApp: staged, destination: destination, pid: 42, relaunch: false)
        #expect(!quiet.contains("open -n"))
        // The pattern is the destination's executable at the start of a command line, every regex
        // character escaped; the app's headless modes are filtered out.
        #expect(quiet.contains("pgrep -lf '^/Applications/Mighty \\(it'\\''s\\) \\[1\\]\\.app/Contents/MacOS/MightyClaude( |$)'"))
        #expect(quiet.contains("grep -Ev -- ' (--agent-io-mcp|--agent-delegation-mcp|--verify-resources)( |$)'"))
        #expect(quiet.contains("OLD=\"$DESTINATION.update-old\"") && quiet.contains("mv \"$DESTINATION\" \"$OLD\"") && quiet.contains("mv \"$NEW\" \"$DESTINATION\"") && quiet.contains("rm -rf \"$OLD\""))
        // The running check comes after the new copy is ready and before the old app is moved aside.
        let check = quiet.range(of: "pgrep -lf")!.lowerBound
        #expect(quiet.range(of: "ditto \"$SOURCE\" \"$NEW\"")!.lowerBound < check)
        #expect(check < quiet.range(of: "mv \"$DESTINATION\" \"$OLD\"")!.lowerBound)
        // A failed swap puts the old app back, and says so when even that fails.
        #expect(quiet.contains("mv \"$OLD\" \"$DESTINATION\" || echo \"Could not restore the previous version"))
        // Signals cannot stop the helper between the two renames.
        let ignore = quiet.range(of: "trap '' HUP INT TERM")!.lowerBound, restore = quiet.range(of: "trap - HUP INT TERM")!.lowerBound
        #expect(ignore < quiet.range(of: "mv \"$DESTINATION\" \"$OLD\"")!.lowerBound && quiet.range(of: "mv \"$NEW\" \"$DESTINATION\"")!.lowerBound < restore)
        // A leftover that cannot be removed stops the update rather than letting `mv` move the app into it.
        #expect(quiet.contains("if [ -e \"$OLD\" ]; then echo \"Could not remove the leftover $OLD; skipping the update.\" >&2; rm -rf \"$NEW\"; exit 1; fi"))
        // The old bundle leaves LaunchServices before it is deleted.
        #expect(quiet.contains("\"$LSREGISTER\" -u \"$OLD\""))
        // A helper killed between the renames anyway is repaired by the next one, before anything else.
        let repair = quiet.range(of: "if [ ! -e \"$DESTINATION\" ] && [ -d \"$OLD\" ]; then")!.lowerBound
        #expect(repair < quiet.range(of: "kill -0 \"$PID\"")!.lowerBound)
        // No copy into a temporary backup any more: the swap is two renames.
        #expect(!quiet.contains("BACKUP") && !quiet.contains("MightyClaude.app.bak"))

        let relaunching = AppUpdateService.installScript(stagedApp: staged, destination: destination, pid: 42, relaunch: true)
        #expect(relaunching.contains("open -n \"$DESTINATION\""))
        #expect(!relaunching.contains("pgrep") && relaunching.contains("trap '' HUP INT TERM"))
        #expect(relaunching.contains("OLD=\"$DESTINATION.update-old\"") && relaunching.contains("rm -rf \"$OLD\""))
    }

    /// The guard against a running app, run for real against fake apps in a temporary folder:
    /// the destination's executable is a re-signed copy of bash that waits on its standard input,
    /// started once plainly (the app's window) and once in a headless mode (`--agent-io-mcp`, an
    /// MCP child that may outlive the window). Only the plain one keeps the update staged. The
    /// path has spaces, parentheses and brackets, so this also checks the escaping against the real
    /// BSD `pgrep`.
    @Test func installOnQuitWaitsForTheAppButNotForItsHeadlessChildren() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("app-onquit-" + UUID().uuidString, isDirectory: true)
        var children: [(Process, Pipe)] = []
        defer {
            for (child, input) in children where child.isRunning { child.terminate(); try? input.fileHandleForWriting.close(); child.waitUntilExit() }
            try? FileManager.default.removeItem(at: root)
        }
        let staged = try makeFakeApp(at: root.appendingPathComponent("updates/0.2.0/staged", isDirectory: true), version: "0.2.0")
        let installed = try makeFakeApp(at: root.appendingPathComponent("X (1) [a]", isDirectory: true), version: "0.1.0")
        let binary = installed.appendingPathComponent("Contents/MacOS/MightyClaude")
        try FileManager.default.removeItem(at: binary)
        try FileManager.default.copyItem(at: URL(fileURLWithPath: "/bin/bash"), to: binary)
        // A copied system binary is killed at launch unless it carries its own (ad-hoc) signature.
        let sign = try await AppUpdateService.run("/usr/bin/codesign", ["-f", "-s", "-", binary.path])
        #expect(sign.exitCode == 0, "codesign: \(String(decoding: sign.stderr, as: UTF8.self))")
        func start(_ arguments: [String]) throws -> Process {
            let child = Process(), input = Pipe()
            child.executableURL = binary
            child.arguments = arguments
            child.standardInput = input
            child.standardOutput = FileHandle.nullDevice
            child.standardError = FileHandle.nullDevice
            try child.run()
            children.append((child, input))
            return child
        }
        func stop(_ child: Process) { child.terminate(); child.waitUntilExit() }
        let gone = Process()
        gone.executableURL = URL(fileURLWithPath: "/usr/bin/true")
        try gone.run(); gone.waitUntilExit()
        let helper = root.appendingPathComponent("install.sh")
        try Data(AppUpdateService.installScript(stagedApp: staged, destination: installed, pid: gone.processIdentifier, relaunch: false).utf8).write(to: helper)
        func version() throws -> String? {
            let plist = try PropertyListSerialization.propertyList(from: Data(contentsOf: installed.appendingPathComponent("Contents/Info.plist")), format: nil) as? [String: Any]
            return plist?["CFBundleShortVersionString"] as? String
        }

        // The app itself is running again from the destination: nothing is replaced.
        let window = try start([])
        #expect(window.isRunning)
        let skipped = try await AppUpdateService.run("/bin/sh", [helper.path])
        let skippedLog = String(decoding: skipped.stdout + skipped.stderr, as: UTF8.self)
        #expect(skipped.exitCode == 0 && skippedLog.contains("running again"), "\(skippedLog)")
        #expect(try version() == "0.1.0")
        #expect(FileManager.default.fileExists(atPath: staged.path)) // still staged for the next quit
        #expect(!FileManager.default.fileExists(atPath: installed.path + ".update-new"))
        #expect(window.isRunning) // it was there the whole time
        stop(window)

        // Only a headless child of the app is left: the update goes in.
        // `--` keeps bash from reading the flag as one of its own options and exiting.
        let child = try start(["-s", "--", PaneMCPServerLocation.headlessArgument])
        let done = try await AppUpdateService.run("/bin/sh", [helper.path])
        let doneLog = String(decoding: done.stdout + done.stderr, as: UTF8.self)
        #expect(done.exitCode == 0 && doneLog.contains("Installed: "), "\(doneLog)")
        #expect(try version() == "0.2.0")
        #expect(!FileManager.default.fileExists(atPath: installed.path + ".update-old"))
        #expect(child.isRunning) // it was there the whole time, and did not hold the update back
        stop(child)
    }
}
