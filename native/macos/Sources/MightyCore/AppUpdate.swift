import CryptoKit
import Foundation

/// Self-update: a JSON manifest at a fixed HTTPS location (Cloudflare) names
/// the current release and where its packages are; the app compares the
/// manifest version with its own, downloads the macOS package, verifies it,
/// unpacks it, and hands the swap to a helper that waits for the app to quit.
///
/// Trust: the manifest is the root, so it is Ed25519-signed by the release
/// job and the public key is compiled into the app (`MightyUpdatePublicKey`).
/// A build with a key rejects unsigned or mis-signed manifests; transport is
/// https only, including every redirect hop.
public struct AppUpdateAsset: Sendable, Equatable {
    public var url: URL
    public var sha256: String?
    public var size: Int?
    public init(url: URL, sha256: String? = nil, size: Int? = nil) { self.url = url; self.sha256 = sha256; self.size = size }
}

public struct AppUpdateManifest: Sendable, Equatable {
    public static let envelopeFormat = "mightyclaude-update-v1"
    public var version: String
    public var build: Int?
    public var notes: String?
    public var publishedAt: String?
    public var minimumSystemVersion: String?
    public var macos: AppUpdateAsset?
    /// Windows packages by architecture (`x64`, `arm64`); kept for the Windows client.
    public var windows: [String: AppUpdateAsset]
    /// True when the manifest came in a signed envelope that verified.
    public var signed = false
    public init(version: String, build: Int? = nil, notes: String? = nil, publishedAt: String? = nil, minimumSystemVersion: String? = nil, macos: AppUpdateAsset? = nil, windows: [String: AppUpdateAsset] = [:], signed: Bool = false) {
        self.version = version; self.build = build; self.notes = notes; self.publishedAt = publishedAt; self.minimumSystemVersion = minimumSystemVersion; self.macos = macos; self.windows = windows; self.signed = signed
    }

    /// `latest.json` is either a signed envelope
    /// `{"format":"mightyclaude-update-v1","payload":"<base64 JSON>","signature":"<base64 Ed25519>"}`
    /// or, for builds without a public key, the plain manifest. With a key,
    /// only an envelope whose signature verifies is accepted.
    public static func parse(_ data: Data, publicKey: Data? = nil, allowsFileURLs: Bool = false) throws -> AppUpdateManifest {
        guard data.count <= 256 * 1024 else { throw MightyError("업데이트 정보 파일이 너무 큽니다.") }
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { throw MightyError("업데이트 정보 파일을 JSON 객체로 읽지 못했습니다.") }
        if let payload = object["payload"] as? String, let signature = object["signature"] as? String {
            guard object["format"] as? String == envelopeFormat else { throw MightyError("업데이트 정보의 서명 형식을 알 수 없습니다.") }
            guard let payloadBytes = Data(base64Encoded: payload), let signatureBytes = Data(base64Encoded: signature) else { throw MightyError("업데이트 정보의 서명 인코딩이 잘못되었습니다.") }
            var verified = false
            if let publicKey {
                guard let key = try? Curve25519.Signing.PublicKey(rawRepresentation: publicKey), key.isValidSignature(signatureBytes, for: payloadBytes) else {
                    throw MightyError("업데이트 정보의 서명이 이 앱의 공개 키와 맞지 않습니다.")
                }
                verified = true
            }
            var manifest = try parsePlain(payloadBytes, allowsFileURLs: allowsFileURLs)
            manifest.signed = verified
            return manifest
        }
        guard publicKey == nil else { throw MightyError("서명되지 않은 업데이트 정보입니다. 이 앱은 서명된 정보만 받습니다.") }
        return try parsePlain(data, allowsFileURLs: allowsFileURLs)
    }

    /// Accepts the documented shape and a few natural variants:
    /// `{"version","macos":{"url","sha256","size"},"windows":{"x64":{…}}}`,
    /// assets under `"platforms"`/`"downloads"`, a bare `"macos":"https://…"`,
    /// and `"url"` as `"download"`/`"downloadUrl"`. Package URLs must be https
    /// (or a file URL, for tests).
    static func parsePlain(_ data: Data, allowsFileURLs: Bool) throws -> AppUpdateManifest {
        guard data.count <= 256 * 1024, let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { throw MightyError("업데이트 정보를 JSON 객체로 읽지 못했습니다.") }
        guard let rawVersion = (object["version"] ?? object["latest"] ?? object["latestVersion"]) as? String,
              let version = AppVersion.normalized(rawVersion) else { throw MightyError("업데이트 정보에 유효한 version이 없습니다.") }
        let assets = (object["platforms"] ?? object["downloads"] ?? object["assets"]) as? [String: Any] ?? object
        func asset(_ value: Any?) -> AppUpdateAsset? {
            // Bare string URLs are always rejected: they cannot carry sha256 or size.
            guard let dictionary = value as? [String: Any],
                  let text = (dictionary["url"] ?? dictionary["download"] ?? dictionary["downloadUrl"] ?? dictionary["href"]) as? String,
                  let url = URL(string: text), allowed(url, allowsFileURLs: allowsFileURLs) else { return nil }
            let digest = (dictionary["sha256"] as? String).flatMap { value -> String? in
                let clean = value.lowercased().trimmingCharacters(in: .whitespacesAndNewlines).replacingOccurrences(of: "sha256:", with: "")
                return clean.count == 64 && clean.allSatisfy(\.isHexDigit) ? clean : nil
            }
            let size = (dictionary["size"] as? Int).flatMap { $0 > 0 ? $0 : nil }
            // Rule 2: both sha256 and size are required; reject assets missing either.
            guard let digest, let size else { return nil }
            return AppUpdateAsset(url: url, sha256: digest, size: size)
        }
        var windows: [String: AppUpdateAsset] = [:]
        if let table = (assets["windows"] ?? assets["win"]) as? [String: Any] {
            if let single = asset(table) { windows["x64"] = single }
            for (key, value) in table where ["x64", "arm64"].contains(key) { if let item = asset(value) { windows[key] = item } }
        }
        let build = (object["build"] as? Int) ?? (object["buildNumber"] as? Int)
        return AppUpdateManifest(version: version, build: build.flatMap { $0 >= 0 ? $0 : nil },
                                 notes: (object["notes"] ?? object["releaseNotes"] ?? object["changelog"]) as? String,
                                 publishedAt: (object["publishedAt"] ?? object["date"]) as? String,
                                 minimumSystemVersion: ((object["minimumSystemVersion"] ?? object["minOS"]) as? String).flatMap(AppVersion.normalized),
                                 macos: asset(assets["macos"] ?? assets["mac"] ?? assets["darwin"]), windows: windows)
    }
    public static func allowed(_ url: URL, allowsFileURLs: Bool) -> Bool {
        guard let scheme = url.scheme?.lowercased() else { return false }
        if scheme == "https" { return url.host?.isEmpty == false }
        return scheme == "file" && allowsFileURLs
    }
}

/// Dotted numeric versions with an optional pre-release tag: `1.2.0` >
/// `1.2.0-beta.2` > `1.1.9`; missing components read as 0.
public enum AppVersion {
    public static func normalized(_ value: String) -> String? {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        let stripped = trimmed.hasPrefix("v") || trimmed.hasPrefix("V") ? String(trimmed.dropFirst()) : trimmed
        guard stripped.range(of: "^[0-9]+(\\.[0-9]+){0,3}(-[0-9A-Za-z.-]+)?(\\+[0-9A-Za-z.-]+)?$", options: .regularExpression) != nil, stripped.count <= 64 else { return nil }
        return stripped
    }
    public static func compare(_ lhs: String, _ rhs: String) -> ComparisonResult {
        guard let left = normalized(lhs), let right = normalized(rhs) else { return .orderedSame }
        func split(_ value: String) -> ([Int], String?) {
            let core = value.split(separator: "+", maxSplits: 1)[0]
            let parts = core.split(separator: "-", maxSplits: 1)
            let numbers = parts[0].split(separator: ".").map { Int($0) ?? 0 }
            return (numbers, parts.count > 1 ? String(parts[1]) : nil)
        }
        let (a, aPre) = split(left), (b, bPre) = split(right)
        for index in 0..<max(a.count, b.count) {
            let x = index < a.count ? a[index] : 0, y = index < b.count ? b[index] : 0
            if x != y { return x < y ? .orderedAscending : .orderedDescending }
        }
        switch (aPre, bPre) {
        case (nil, nil): return .orderedSame
        case (nil, _?): return .orderedDescending
        case (_?, nil): return .orderedAscending
        case let (x?, y?): return x == y ? .orderedSame : (x.compare(y, options: .numeric) == .orderedAscending ? .orderedAscending : .orderedDescending)
        }
    }
    public static func isNewer(_ candidate: String, than current: String) -> Bool { compare(candidate, current) == .orderedDescending }
    public static var systemVersion: String {
        let os = ProcessInfo.processInfo.operatingSystemVersion
        return "\(os.majorVersion).\(os.minorVersion).\(os.patchVersion)"
    }
}

public struct AppUpdateAvailability: Sendable, Equatable {
    public var current: String
    public var manifest: AppUpdateManifest
    /// A newer version exists and this Mac can run it.
    public var isNewer: Bool
    /// Why a newer version cannot be installed here (macOS too old).
    public var blockedReason: String?
    public init(current: String, manifest: AppUpdateManifest, systemVersion: String = AppVersion.systemVersion) {
        self.current = current; self.manifest = manifest
        let newer = AppVersion.isNewer(manifest.version, than: current)
        if newer, let minimum = manifest.minimumSystemVersion, AppVersion.isNewer(minimum, than: systemVersion) {
            isNewer = false; blockedReason = "새 버전 \(manifest.version)은 macOS \(minimum) 이상이 필요합니다. 이 Mac은 \(systemVersion)입니다."
        } else { isNewer = newer; blockedReason = nil }
    }
}

/// Refuses redirects that leave https (ATS is disabled in this app), and
/// forwards download progress.
final class AppUpdateTransportPolicy: NSObject, URLSessionTaskDelegate, URLSessionDownloadDelegate, @unchecked Sendable {
    let allowsFileURLs: Bool
    let progress: (@Sendable (Double) -> Void)?
    init(allowsFileURLs: Bool, progress: (@Sendable (Double) -> Void)? = nil) { self.allowsFileURLs = allowsFileURLs; self.progress = progress }
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(request.url.map { AppUpdateManifest.allowed($0, allowsFileURLs: allowsFileURLs) } == true ? request : nil)
    }
    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64, totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
        guard totalBytesExpectedToWrite > 0 else { return }
        progress?(min(0.99, Double(totalBytesWritten) / Double(totalBytesExpectedToWrite)))
    }
    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {}
}

public actor AppUpdateService {
    public static let maximumPackageBytes = 512 * 1024 * 1024
    public static let packageName = "MightyClaude-macos.zip"
    private let session: URLSession
    private let directory: URL
    private let publicKey: Data?
    /// Tests point the manifest and package at local files.
    let allowsFileURLs: Bool
    private var downloadTask: Task<URL, Error>?

    public init(directory: URL, publicKey: Data? = nil, session: URLSession? = nil, allowsFileURLs: Bool = false) {
        self.directory = directory; self.publicKey = publicKey; self.allowsFileURLs = allowsFileURLs
        if let session { self.session = session }
        else {
            let configuration = URLSessionConfiguration.ephemeral
            configuration.timeoutIntervalForRequest = 30
            configuration.timeoutIntervalForResource = 30 * 60
            configuration.waitsForConnectivity = false
            self.session = URLSession(configuration: configuration)
        }
    }

    public var verifiesSignatures: Bool { publicKey != nil }

    public func check(manifestURL: URL, currentVersion: String) async throws -> AppUpdateAvailability {
        guard publicKey != nil else { throw MightyError("이 빌드는 업데이트 확인을 지원하지 않습니다.") }
        guard AppUpdateManifest.allowed(manifestURL, allowsFileURLs: allowsFileURLs) else { throw MightyError("업데이트 정보 주소는 https여야 합니다.") }
        var request = URLRequest(url: manifestURL)
        request.cachePolicy = .reloadIgnoringLocalCacheData
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        let (data, response) = try await session.data(for: request, delegate: AppUpdateTransportPolicy(allowsFileURLs: allowsFileURLs))
        if let http = response as? HTTPURLResponse, !(200..<300).contains(http.statusCode) { throw MightyError("업데이트 정보를 받지 못했습니다. HTTP \(http.statusCode)") }
        guard let final = response.url, AppUpdateManifest.allowed(final, allowsFileURLs: allowsFileURLs) else { throw MightyError("업데이트 정보가 https가 아닌 주소에서 왔습니다.") }
        return AppUpdateAvailability(current: currentVersion, manifest: try AppUpdateManifest.parse(data, publicKey: publicKey, allowsFileURLs: allowsFileURLs))
    }

    /// Downloads the package into `updates/<version>/`, dropping every other
    /// version's folder, then verifies size and SHA-256 of the file on disk.
    public func download(_ asset: AppUpdateAsset, version: String, progress: @escaping @Sendable (Double) -> Void = { _ in }) async throws -> URL {
        guard AppUpdateManifest.allowed(asset.url, allowsFileURLs: allowsFileURLs) else { throw MightyError("패키지 주소는 https여야 합니다.") }
        if let size = asset.size, size > Self.maximumPackageBytes { throw MightyError("패키지가 너무 큽니다.") }
        let name = AppVersion.normalized(version) ?? "unknown"
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        for entry in (try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)) ?? [] { try? FileManager.default.removeItem(at: entry) }
        let folder = directory.appendingPathComponent(name, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        let destination = folder.appendingPathComponent(Self.packageName)
        let policy = AppUpdateTransportPolicy(allowsFileURLs: allowsFileURLs, progress: progress)
        let task = Task<URL, Error> { [session, allowsFileURLs] in
            let (temporary, response) = try await session.download(for: URLRequest(url: asset.url), delegate: policy)
            defer { try? FileManager.default.removeItem(at: temporary) }
            if let http = response as? HTTPURLResponse, !(200..<300).contains(http.statusCode) { throw MightyError("패키지를 받지 못했습니다. HTTP \(http.statusCode)") }
            guard let final = response.url, AppUpdateManifest.allowed(final, allowsFileURLs: allowsFileURLs) else { throw MightyError("패키지가 https가 아닌 주소에서 왔습니다.") }
            try? FileManager.default.removeItem(at: destination)
            try FileManager.default.moveItem(at: temporary, to: destination)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: destination.path)
            let received = (try FileManager.default.attributesOfItem(atPath: destination.path)[.size] as? Int) ?? -1
            guard received >= 0, received <= Self.maximumPackageBytes else { throw MightyError("패키지가 너무 큽니다.") }
            if let size = asset.size, size != received { throw MightyError("패키지 크기가 업데이트 정보와 다릅니다 (\(received) ≠ \(size)).") }
            try Task.checkCancellation()
            if let sha256 = asset.sha256, try Self.fileDigest(destination) != sha256 { throw MightyError("패키지 SHA-256이 업데이트 정보와 다릅니다.") }
            progress(1)
            return destination
        }
        downloadTask = task
        defer { downloadTask = nil }
        do { return try await task.value } catch { try? FileManager.default.removeItem(at: folder); throw error }
    }

    /// SHA-256 of the bytes actually on disk, read in 1 MiB pieces.
    public static func fileDigest(_ url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hasher = SHA256()
        while let chunk = try handle.read(upToCount: 1 << 20), !chunk.isEmpty { hasher.update(data: chunk) }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    public func cancelDownload() { downloadTask?.cancel() }

    /// Unpacks the zip next to it and returns the single `.app` inside after
    /// checking that it is a MightyClaude bundle with a runnable executable.
    /// Symlinks are refused so what was validated is what gets installed.
    public func stage(package: URL, expectedBundleIdentifier: String) async throws -> URL {
        let folder = package.deletingLastPathComponent().appendingPathComponent("staged", isDirectory: true)
        try? FileManager.default.removeItem(at: folder)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let result = try await Self.run("/usr/bin/ditto", ["-x", "-k", package.path, folder.path])
        guard result.exitCode == 0 else { throw MightyError("패키지를 풀지 못했습니다: " + String(decoding: result.stderr, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)) }
        let apps = ((try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: [.isSymbolicLinkKey])) ?? []).filter { $0.pathExtension == "app" }
        guard apps.count == 1, let app = apps.first else { throw MightyError("패키지 안에 앱 번들이 하나 있어야 합니다 (\(apps.count)개).") }
        try Self.validate(app: app, within: folder, expectedBundleIdentifier: expectedBundleIdentifier)
        return app
    }

    /// The identity check, run at stage time and again right before install.
    public static func validate(app: URL, within folder: URL? = nil, expectedBundleIdentifier: String) throws {
        let plist = app.appendingPathComponent("Contents/Info.plist")
        let binaryDirectory = app.appendingPathComponent("Contents/MacOS", isDirectory: true)
        for url in [app, plist, binaryDirectory] {
            if (try? url.resourceValues(forKeys: [.isSymbolicLinkKey]))?.isSymbolicLink == true { throw MightyError("패키지의 앱 번들에 심볼릭 링크가 있어 설치하지 않습니다.") }
        }
        if let folder, !app.resolvingSymlinksInPath().path.hasPrefix(folder.resolvingSymlinksInPath().path + "/") { throw MightyError("패키지의 앱 번들 위치가 올바르지 않습니다.") }
        guard let data = try? Data(contentsOf: plist), let info = try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any],
              info["CFBundleIdentifier"] as? String == expectedBundleIdentifier, let executable = info["CFBundleExecutable"] as? String, !executable.contains("/") else {
            throw MightyError("패키지의 앱 번들이 MightyClaude가 아니거나 손상되었습니다.")
        }
        let binary = binaryDirectory.appendingPathComponent(executable)
        guard (try? binary.resourceValues(forKeys: [.isSymbolicLinkKey]))?.isSymbolicLink != true, FileManager.default.isExecutableFile(atPath: binary.path) else {
            throw MightyError("패키지의 앱 실행 파일이 없거나 실행할 수 없습니다.")
        }
    }

    /// The helper waits for the running app to exit, copies the new bundle
    /// beside the old one, swaps them in one move, keeps a backup until the
    /// swap succeeded, and relaunches. Paths are single-quoted for `sh`.
    ///
    /// Two extra steps keep the macOS input method attached to the right
    /// bundle: the staged copy is unregistered from LaunchServices and the
    /// installed one re-registered, and the relaunch waits two seconds after
    /// the old process is gone. Every build carries a fresh ad-hoc signature,
    /// so duplicate registrations of the same bundle id are what confuse the
    /// text-input session (Korean composition falling apart into jamo).
    public static func installScript(stagedApp: URL, destination: URL, pid: Int32, relaunch: Bool = true) -> String {
        func quote(_ value: String) -> String { "'" + value.replacingOccurrences(of: "'", with: "'\\''") + "'" }
        let stamp = ISO8601DateFormatter().string(from: Date()).replacingOccurrences(of: ":", with: "")
        return """
        #!/bin/sh
        # MightyClaude update helper: swap the bundle only after the app has quit.
        set -u
        PATH=/usr/bin:/bin; export PATH
        SOURCE=\(quote(stagedApp.path))
        DESTINATION=\(quote(destination.path))
        BACKUP=\(quote(NSTemporaryDirectory() + "MightyClaude-app-backup-" + stamp))
        PID=\(pid)
        NEW="$DESTINATION.update-new"
        LSREGISTER=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister
        for _ in $(seq 1 600); do kill -0 "$PID" 2>/dev/null || break; sleep 0.5; done
        if kill -0 "$PID" 2>/dev/null; then echo "MightyClaude가 종료되지 않아 업데이트를 건너뜁니다." >&2; exit 1; fi
        sleep 2
        [ -d "$SOURCE" ] || { echo "설치할 앱이 없습니다: $SOURCE" >&2; exit 1; }
        rm -rf "$NEW"
        ditto "$SOURCE" "$NEW" || { echo "새 앱을 복사하지 못했습니다." >&2; rm -rf "$NEW"; exit 1; }
        if [ -d "$DESTINATION" ]; then
          mkdir -p "$BACKUP" && ditto "$DESTINATION" "$BACKUP/MightyClaude.app.bak" || { echo "백업하지 못했습니다." >&2; rm -rf "$NEW"; exit 1; }
          rm -rf "$DESTINATION"
        fi
        if ! mv "$NEW" "$DESTINATION"; then
          echo "앱을 교체하지 못해 이전 버전을 되돌립니다." >&2
          rm -rf "$DESTINATION" "$NEW"
          [ -d "$BACKUP/MightyClaude.app.bak" ] && ditto "$BACKUP/MightyClaude.app.bak" "$DESTINATION"
          exit 1
        fi
        rm -rf "$BACKUP"
        "$LSREGISTER" -u "$SOURCE" >/dev/null 2>&1 || true
        rm -rf "$(dirname "$SOURCE")"
        "$LSREGISTER" -f "$DESTINATION" >/dev/null 2>&1 || true
        echo "설치 완료: $DESTINATION"
        \(relaunch ? "open -n \"$DESTINATION\"" : "true")
        """
    }

    /// Writes the helper next to the package and starts it detached with a
    /// minimal environment, logging to `install.log` beside it.
    public func launchInstaller(script: String, near package: URL) throws {
        let folder = package.deletingLastPathComponent()
        let path = folder.appendingPathComponent("install.sh")
        try Data(script.utf8).write(to: path, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: path.path)
        let log = folder.appendingPathComponent("install.log")
        guard FileManager.default.createFile(atPath: log.path, contents: nil, attributes: [.posixPermissions: 0o600]) else { throw MightyError("설치 로그 파일을 만들지 못했습니다.") }
        let handle = try FileHandle(forWritingTo: log)
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/sh")
        process.arguments = [path.path]
        process.environment = ["PATH": "/usr/bin:/bin", "HOME": FileManager.default.homeDirectoryForCurrentUser.path]
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = handle
        process.standardError = handle
        try process.run()
        try? handle.close()
    }

    static func run(_ executable: String, _ arguments: [String]) async throws -> ProcessResult {
        let stdout = OutputSink(), stderr = OutputSink()
        let child = try NativeChildProcess(executable: URL(fileURLWithPath: executable), arguments: arguments, environment: ["PATH": "/usr/bin:/bin"], cwd: URL(fileURLWithPath: NSTemporaryDirectory()),
                                           stdout: { stdout.append($0) }, stderr: { stderr.append($0) }, exited: { _ in })
        child.closeInput()
        let code = await child.wait(timeout: 300)
        return ProcessResult(exitCode: code, stdout: stdout.data, stderr: stderr.data)
    }
    final class OutputSink: @unchecked Sendable {
        private let lock = NSLock(); private var buffer = Data()
        var data: Data { lock.lock(); defer { lock.unlock() }; return buffer }
        func append(_ chunk: Data) { lock.lock(); if buffer.count < 1_048_576 { buffer.append(chunk) }; lock.unlock() }
    }
}
