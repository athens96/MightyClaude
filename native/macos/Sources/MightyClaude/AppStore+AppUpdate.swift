import AppKit
import Foundation
import MightyCore

/// App self-update driven by a signed JSON manifest on Cloudflare. The
/// manifest address and the Ed25519 public key come from the build
/// (`MightyUpdateManifestURL`, `MightyUpdatePublicKey` in Info.plist); the
/// address can be overridden in Settings. The package is verified, unpacked,
/// and swapped in by a helper only after the app has quit.
extension AppStore {
    enum AppUpdatePhase: Equatable {
        case idle, checking, upToDate, available, downloading(Double), staging, ready, installing
        case failed(String)
    }
    struct AppUpdateState: Equatable {
        var phase: AppUpdatePhase = .idle
        var availability: AppUpdateAvailability?
        var package: URL?
        var stagedApp: URL?
        var checkedAt: Date?
    }
    static let appUpdateURLDefaultsKey = "appUpdate.manifestURL"
    static let appUpdateLastCheckKey = "appUpdate.lastCheck"
    static let appUpdateAutomaticKey = "appUpdate.automatic"
    static let appUpdateCheckInterval: TimeInterval = 24 * 60 * 60
    static let appBundleIdentifier = "dev.mightyclaude.native"

    var appVersion: String { Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0.2.0" }
    /// The address baked into the build, for the placeholder and the reset.
    var builtInUpdateManifestURL: String? {
        (Bundle.main.infoDictionary?["MightyUpdateManifestURL"] as? String).flatMap { $0.isEmpty ? nil : $0 }
    }
    /// Raw 32-byte Ed25519 public key baked into the build, base64 in Info.plist.
    var appUpdatePublicKey: Data? {
        (Bundle.main.infoDictionary?["MightyUpdatePublicKey"] as? String).flatMap { Data(base64Encoded: $0) }.flatMap { $0.count == 32 ? $0 : nil }
    }
    var appUpdateManifestURLOverride: String {
        get { UserDefaults.standard.string(forKey: Self.appUpdateURLDefaultsKey) ?? "" }
        set { objectWillChange.send(); UserDefaults.standard.set(newValue, forKey: Self.appUpdateURLDefaultsKey) }
    }
    var appUpdateManifestURL: URL? {
        // Rule 3: the stamped address wins; user override is not consulted when the build carries a URL.
        if let builtIn = builtInUpdateManifestURL, !builtIn.isEmpty {
            return URL(string: builtIn).flatMap { $0.scheme?.lowercased() == "https" ? $0 : nil }
        }
        let override = appUpdateManifestURLOverride.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !override.isEmpty else { return nil }
        return URL(string: override).flatMap { $0.scheme?.lowercased() == "https" ? $0 : nil }
    }
    var appUpdateAutomatic: Bool {
        get { UserDefaults.standard.object(forKey: Self.appUpdateAutomaticKey) as? Bool ?? true }
        set { objectWillChange.send(); UserDefaults.standard.set(newValue, forKey: Self.appUpdateAutomaticKey) }
    }

    private var appUpdateService: AppUpdateService {
        if let service = appUpdateServiceStorage { return service }
        let service = AppUpdateService(directory: dataDirectory.appendingPathComponent("updates", isDirectory: true), publicKey: appUpdatePublicKey)
        appUpdateServiceStorage = service
        return service
    }

    /// Once a day when a manifest address is known, the build carries a public key,
    /// and the user has not turned automatic checks off. Never downloads on its own.
    func checkForAppUpdateAutomatically() {
        guard appUpdateAutomatic, !smokeTesting, appUpdatePublicKey != nil, appUpdateManifestURL != nil else { return }
        let last = UserDefaults.standard.object(forKey: Self.appUpdateLastCheckKey) as? Date
        guard last.map({ Date().timeIntervalSince($0) >= Self.appUpdateCheckInterval }) ?? true else { return }
        checkForAppUpdate()
    }

    private var appUpdateBusy: Bool {
        switch appUpdate.phase { case .checking, .downloading, .staging, .installing: return true; default: return false }
    }

    func checkForAppUpdate() {
        guard !ending, !appUpdateBusy else { return }
        guard let url = appUpdateManifestURL else { appUpdate.phase = .failed("업데이트 정보 주소가 설정되지 않았습니다. 설정에서 https 주소를 입력하세요."); return }
        appUpdate.phase = .checking
        let version = appVersion
        let service = appUpdateService
        Task { [weak self] in
            let outcome: Result<AppUpdateAvailability, Error>
            do { outcome = .success(try await service.check(manifestURL: url, currentVersion: version)) } catch { outcome = .failure(error) }
            await MainActor.run {
                guard let self else { return }
                switch outcome {
                case .success(let availability):
                    UserDefaults.standard.set(Date(), forKey: Self.appUpdateLastCheckKey)
                    self.appUpdate.checkedAt = Date()
                    self.appUpdate.availability = availability
                    self.appUpdate.package = nil; self.appUpdate.stagedApp = nil
                    if let reason = availability.blockedReason { self.appUpdate.phase = .failed(reason) }
                    else { self.appUpdate.phase = availability.isNewer && availability.manifest.macos != nil ? .available : .upToDate }
                case .failure(let error):
                    self.appUpdate.phase = .failed(error.localizedDescription)
                }
            }
        }
    }

    func downloadAppUpdate() {
        guard !ending, !appUpdateBusy, let availability = appUpdate.availability, availability.isNewer, let asset = availability.manifest.macos else { return }
        appUpdate.phase = .downloading(0)
        let service = appUpdateService
        let version = availability.manifest.version
        let store = AppUpdateProgressSink(self)
        Task {
            do {
                let package = try await service.download(asset, version: version) { fraction in store.report(fraction) }
                await MainActor.run { store.owner?.appUpdate.phase = .staging }
                let staged = try await service.stage(package: package, expectedBundleIdentifier: Self.appBundleIdentifier)
                await MainActor.run {
                    guard let owner = store.owner else { return }
                    owner.appUpdate.package = package; owner.appUpdate.stagedApp = staged; owner.appUpdate.phase = .ready
                }
            } catch is CancellationError {
                await MainActor.run { store.owner?.appUpdate.phase = .available }
            } catch let error as URLError where error.code == .cancelled {
                await MainActor.run { store.owner?.appUpdate.phase = .available }
            } catch {
                await MainActor.run { store.owner?.appUpdate.phase = .failed(error.localizedDescription) }
            }
        }
    }

    func cancelAppUpdateDownload() {
        guard case .downloading = appUpdate.phase else { return }
        let service = appUpdateService
        Task { await service.cancelDownload() }
    }

    /// Re-validates the staged bundle and the package hash, starts the helper and quits.
    /// The helper waits for this process to end, swaps the bundle at the running
    /// app's own path, and relaunches it.
    func installAppUpdateAndRelaunch() {
        guard !ending, case .ready = appUpdate.phase, let staged = appUpdate.stagedApp, let package = appUpdate.package else { return }
        guard let asset = appUpdate.availability?.manifest.macos else {
            appUpdate.phase = .failed("설치할 패키지 정보를 찾을 수 없습니다."); return
        }
        let destination = Bundle.main.bundleURL
        guard destination.pathExtension == "app", FileManager.default.isWritableFile(atPath: destination.deletingLastPathComponent().path) else {
            appUpdate.phase = .failed("실행 중인 앱의 위치(\(destination.path))에 쓸 수 없어 교체할 수 없습니다."); return
        }
        do { try AppUpdateService.validate(app: staged, within: package.deletingLastPathComponent(), expectedBundleIdentifier: Self.appBundleIdentifier) }
        catch { appUpdate.phase = .failed("설치 직전 확인에 실패했습니다: \(error.localizedDescription) 다시 다운로드하세요."); appUpdate.stagedApp = nil; return }
        appUpdate.phase = .installing
        let script = AppUpdateService.installScript(stagedApp: staged, destination: destination, pid: ProcessInfo.processInfo.processIdentifier)
        let service = appUpdateService
        let store = AppUpdateProgressSink(self)
        Task {
            do {
                // Rule 4: re-verify the package on disk immediately before replacement.
                if let size = asset.size {
                    let actual = (try? FileManager.default.attributesOfItem(atPath: package.path)[.size] as? Int) ?? -1
                    if actual != size { throw MightyError("패키지 크기가 업데이트 정보와 다릅니다 — 다시 다운로드하세요.") }
                }
                if let sha256 = asset.sha256, try AppUpdateService.fileDigest(package) != sha256 {
                    throw MightyError("패키지 SHA-256이 업데이트 정보와 다릅니다 — 다시 다운로드하세요.")
                }
                try await service.launchInstaller(script: script, near: package)
                await MainActor.run { NSApp.terminate(nil) }
            } catch {
                await MainActor.run { store.owner?.appUpdate.phase = .failed("업데이트를 설치하지 못했습니다: \(error.localizedDescription)") }
            }
        }
    }
}

/// A weak handle the download task can carry across actors without
/// capturing the store itself.
final class AppUpdateProgressSink: @unchecked Sendable {
    weak var owner: AppStore?
    init(_ owner: AppStore) { self.owner = owner }
    func report(_ fraction: Double) {
        Task { @MainActor in
            guard let owner, case .downloading = owner.appUpdate.phase else { return }
            owner.appUpdate.phase = .downloading(fraction)
        }
    }
}
